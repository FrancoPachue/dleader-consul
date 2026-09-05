using System.Collections.Concurrent;
using DLeader.Consul.Abstractions;
using DLeader.Consul.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace DLeader.Consul.IntegrationTests;

/// <summary>
/// The hosted-service base class, driven the way a host drives it.
/// </summary>
/// <remarks>
/// It shipped in 2.0 with no test of its own, on the reasoning that it only composes
/// the lease API. That was true and still left its one piece of logic - what reason
/// it reports when a term ends - unverified, and that logic was wrong: it read the
/// reason before disposing the lease, so every voluntary return reported nothing.
/// </remarks>
[Collection(ConsulCollection.Name)]
public class LeaderElectedServiceTests
{
    private readonly ConsulContainer _consul;

    public LeaderElectedServiceTests(ConsulContainer consul) => _consul = consul;

    /// <summary>
    /// Records what the base class hands it. The first term ends when the test says
    /// so; later terms just wait to be cancelled, since the base class re-acquires as
    /// soon as a term ends.
    /// </summary>
    private sealed class Probe : LeaderElectedService
    {
        private int _terms;

        public TaskCompletionSource FirstTermStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource EndFirstTerm { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<long> FencingTokens { get; } = new();

        public ConcurrentQueue<LeadershipLostReason?> Reasons { get; } = new();

        public Probe(ILeadershipLeaseProvider leases)
            : base(leases, NullLogger<Probe>.Instance) { }

        protected override async Task ExecuteAsLeaderAsync(ILeadershipLease lease, CancellationToken ct)
        {
            FencingTokens.Enqueue(lease.FencingToken);

            if (Interlocked.Increment(ref _terms) == 1)
            {
                FirstTermStarted.TrySetResult();
                await EndFirstTerm.Task.WaitAsync(ct);
                return;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }

        protected override Task OnLeadershipEndedAsync(LeadershipLostReason? reason)
        {
            Reasons.Enqueue(reason);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ExecuteAsLeaderAsync_ReceivesALease_WithAFencingToken()
    {
        var serviceName = ConsulContainer.NewServiceName();
        await using var node = _consul.CreateNode(serviceName, servicePort: 5001);
        var probe = new Probe(node);

        await probe.StartAsync(CancellationToken.None);
        try
        {
            await probe.FirstTermStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.True(probe.FencingTokens.TryPeek(out var token));
            Assert.True(token > 0);
        }
        finally
        {
            await probe.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ATermThatReturnsVoluntarily_ReportsReleased_NotNull()
    {
        var serviceName = ConsulContainer.NewServiceName();
        await using var node = _consul.CreateNode(serviceName, servicePort: 5001);
        var probe = new Probe(node);

        await probe.StartAsync(CancellationToken.None);
        try
        {
            await probe.FirstTermStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

            probe.EndFirstTerm.TrySetResult();

            await WaitUntilAsync(() => !probe.Reasons.IsEmpty, TimeSpan.FromSeconds(30),
                "OnLeadershipEndedAsync was never called after the term returned");

            Assert.True(probe.Reasons.TryPeek(out var reason));

            // Not null: the hook is documented never to receive null, and a term that
            // ended by returning is the one case where reading too early produced it.
            Assert.Equal(LeadershipLostReason.Released, reason);
        }
        finally
        {
            await probe.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task HostShutdown_ReportsCancelled()
    {
        var serviceName = ConsulContainer.NewServiceName();
        await using var node = _consul.CreateNode(serviceName, servicePort: 5001);
        var probe = new Probe(node);

        await probe.StartAsync(CancellationToken.None);
        await probe.FirstTermStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Stopping the service cancels the stopping token, which the lease is linked
        // to. Nothing in Consul changed, so no detector fired: this is the host's
        // decision, and it should be reported as one.
        await probe.StopAsync(CancellationToken.None);

        await WaitUntilAsync(() => !probe.Reasons.IsEmpty, TimeSpan.FromSeconds(30),
            "OnLeadershipEndedAsync was never called on shutdown");

        Assert.True(probe.Reasons.TryPeek(out var reason));
        Assert.Equal(LeadershipLostReason.Cancelled, reason);
    }

    [Fact]
    public async Task LosingTheSession_ReportsTheDetectorsReason_AndTheServiceReacquires()
    {
        var serviceName = ConsulContainer.NewServiceName();
        await using var node = _consul.CreateNode(serviceName, servicePort: 5001);
        var probe = new Probe(node);

        await probe.StartAsync(CancellationToken.None);
        try
        {
            await probe.FirstTermStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

            await DestroySessionOfAsync(node.InstanceId);

            await WaitUntilAsync(() => !probe.Reasons.IsEmpty, TimeSpan.FromSeconds(30),
                "the service never noticed its session was destroyed");

            Assert.True(probe.Reasons.TryPeek(out var reason));
            Assert.Equal(LeadershipLostReason.LockKeyTaken, reason);

            // And it goes back to acquiring rather than staying down: a second term
            // starts, with a strictly higher fencing token.
            await WaitUntilAsync(() => probe.FencingTokens.Count >= 2, TimeSpan.FromSeconds(60),
                "the service never re-acquired leadership after losing it");

            var tokens = probe.FencingTokens.ToArray();
            Assert.True(tokens[1] > tokens[0],
                $"fencing token went from {tokens[0]} to {tokens[1]} across a re-acquisition");
        }
        finally
        {
            await probe.StopAsync(CancellationToken.None);
        }
    }

    private async Task DestroySessionOfAsync(string instanceId)
    {
        using var client = _consul.CreateClient();

        var sessions = await client.Session.List();
        var target = sessions.Response.SingleOrDefault(s => s.Name == instanceId);

        Assert.NotNull(target);
        await client.Session.Destroy(target!.ID);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string because)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(200);
        }

        Assert.Fail(because);
    }
}
