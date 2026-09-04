using System.Diagnostics;
using Consul;
using DLeader.Consul.Abstractions;

namespace DLeader.Consul.IntegrationTests;

[Collection(ConsulCollection.Name)]
public class LeadershipLeaseTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly ConsulContainer _consul;

    public LeadershipLeaseTests(ConsulContainer consul) => _consul = consul;

    [Fact]
    public async Task TwoInstancesCompeting_ExactlyOneAcquiresTheLease()
    {
        var serviceName = ConsulContainer.NewServiceName();

        await using var nodeA = _consul.CreateNode(serviceName, servicePort: 5001);
        await using var nodeB = _consul.CreateNode(serviceName, servicePort: 5002);

        // Both attempt at the same time, which is the case a check-then-act API cannot
        // resolve and a lock can.
        var attempts = await Task.WhenAll(
            nodeA.TryAcquireLeadershipAsync(),
            nodeB.TryAcquireLeadershipAsync());

        var winners = attempts.Where(l => l is not null).ToArray();

        Assert.Single(winners);
        Assert.Contains(attempts, l => l is null);

        var winner = winners[0]!;
        Assert.False(winner.LostToken.IsCancellationRequested);
        Assert.True(winner.FencingToken > 0);

        foreach (var lease in attempts.Where(l => l is not null))
        {
            await lease!.DisposeAsync();
        }
    }

    [Fact]
    public async Task ManyInstancesCompeting_ExactlyOneAcquiresTheLease()
    {
        var serviceName = ConsulContainer.NewServiceName();
        var nodes = Enumerable.Range(5001, 8)
            .Select(port => _consul.CreateNode(serviceName, port))
            .ToArray();

        try
        {
            var attempts = await Task.WhenAll(nodes.Select(n => n.TryAcquireLeadershipAsync()));
            var winners = attempts.Where(l => l is not null).ToArray();

            Assert.Single(winners);

            foreach (var lease in winners)
            {
                await lease!.DisposeAsync();
            }
        }
        finally
        {
            foreach (var node in nodes)
            {
                await node.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Verifies the mitigation, not a universal guarantee. The name says "under
    /// controlled failure" because the ordering asserted here holds only while this
    /// process is scheduled: a stop-the-world pause on the old leader would delay its
    /// cancellation past the successor's acquisition, and nothing in the library can
    /// prevent that. That is what the fencing token is for.
    /// </summary>
    [Fact]
    public async Task LostToken_IsCancelled_BeforeOtherNodeCanAcquire_UnderControlledFailure()
    {
        var serviceName = ConsulContainer.NewServiceName();

        await using var nodeA = _consul.CreateNode(serviceName, servicePort: 5001);
        await using var nodeB = _consul.CreateNode(serviceName, servicePort: 5002);

        var leaseA = await nodeA.TryAcquireLeadershipAsync();
        Assert.NotNull(leaseA);

        var clock = Stopwatch.StartNew();
        TimeSpan? cancelledAt = null;
        leaseA!.LostToken.Register(() => cancelledAt ??= clock.Elapsed);

        // Consul decides the session is dead. Destroying it out of band reproduces that
        // without having to wait out a real TTL, and leaves the agent up so that the
        // successor can genuinely compete.
        await DestroySessionOfAsync(serviceName, nodeA.InstanceId);

        var acquiredAt = await PollUntilAcquiredAsync(nodeB, clock);

        Assert.True(acquiredAt.HasValue, "node B never acquired the lease");
        Assert.True(cancelledAt.HasValue, "node A never observed the loss");
        Assert.True(
            cancelledAt < acquiredAt,
            $"node A observed the loss at {cancelledAt} but node B acquired at {acquiredAt}");

        await leaseA.DisposeAsync();
    }

    [Fact]
    public async Task FencingToken_OfNewLeader_IsStrictlyGreaterThanPrevious()
    {
        var serviceName = ConsulContainer.NewServiceName();

        await using var nodeA = _consul.CreateNode(serviceName, servicePort: 5001);
        await using var nodeB = _consul.CreateNode(serviceName, servicePort: 5002);

        var leaseA = await nodeA.TryAcquireLeadershipAsync();
        Assert.NotNull(leaseA);
        var tokenA = leaseA!.FencingToken;

        await DestroySessionOfAsync(serviceName, nodeA.InstanceId);

        var leaseB = await PollForLeaseAsync(nodeB);
        Assert.NotNull(leaseB);

        Assert.True(
            leaseB!.FencingToken > tokenA,
            $"expected the successor's fencing token to exceed {tokenA}, got {leaseB.FencingToken}");

        await leaseA.DisposeAsync();
        await leaseB.DisposeAsync();
    }

    [Fact]
    public async Task FencingToken_IncreasesAcrossEveryHandover()
    {
        var serviceName = ConsulContainer.NewServiceName();
        var tokens = new List<long>();

        for (var round = 0; round < 4; round++)
        {
            await using var node = _consul.CreateNode(serviceName, servicePort: 5001 + round);
            var lease = await PollForLeaseAsync(node);

            Assert.NotNull(lease);
            tokens.Add(lease!.FencingToken);

            // Clean handover: dispose releases the lock and destroys the session.
            await lease.DisposeAsync();
        }

        Assert.Equal(tokens.OrderBy(t => t).Distinct().ToList(), tokens);
    }

    [Fact]
    public async Task FormerLeader_DoesNotBelieveItIsStillLeader_AfterLosingTheSession()
    {
        var serviceName = ConsulContainer.NewServiceName();

        await using var nodeA = _consul.CreateNode(serviceName, servicePort: 5001);
        await using var nodeB = _consul.CreateNode(serviceName, servicePort: 5002);

        var leaseA = await nodeA.TryAcquireLeadershipAsync();
        Assert.NotNull(leaseA);

        await DestroySessionOfAsync(serviceName, nodeA.InstanceId);

        var leaseB = await PollForLeaseAsync(nodeB);
        Assert.NotNull(leaseB);

        // The old leader knows it is out.
        Assert.True(leaseA!.LostToken.IsCancellationRequested);

        // Coming back does not make it the leader again while the successor holds it.
        var reacquired = await nodeA.TryAcquireLeadershipAsync();
        Assert.Null(reacquired);

        // And the advisory view agrees rather than naming the stale value left on the
        // key by the Release-behaviour session.
        Assert.Equal(nodeB.InstanceId, await nodeA.GetCurrentLeaderAsync());

        await leaseA.DisposeAsync();
        await leaseB!.DisposeAsync();
    }

    [Fact]
    public async Task DisposingTheLease_LetsTheNextInstanceAcquireImmediately()
    {
        var serviceName = ConsulContainer.NewServiceName();

        await using var nodeA = _consul.CreateNode(serviceName, servicePort: 5001);
        await using var nodeB = _consul.CreateNode(serviceName, servicePort: 5002);

        var leaseA = await nodeA.TryAcquireLeadershipAsync();
        Assert.NotNull(leaseA);

        Assert.Null(await nodeB.TryAcquireLeadershipAsync());

        // A clean release destroys the session, so no lock delay applies and the
        // successor takes over without waiting.
        await leaseA!.DisposeAsync();
        Assert.True(leaseA.LostToken.IsCancellationRequested);

        var leaseB = await PollForLeaseAsync(nodeB, TimeSpan.FromSeconds(10));
        Assert.NotNull(leaseB);

        await leaseB!.DisposeAsync();
    }

    // -----------------------------------------------------------------------------

    /// <summary>
    /// Destroys the Consul session a node's lease is holding, which is what Consul
    /// itself does when a TTL runs out.
    /// </summary>
    private async Task DestroySessionOfAsync(string serviceName, string instanceId)
    {
        using var client = _consul.CreateClient();

        var sessions = await client.Session.List();
        var target = sessions.Response.SingleOrDefault(s => s.Name == instanceId);

        Assert.NotNull(target);
        await client.Session.Destroy(target!.ID);
    }

    private static async Task<TimeSpan?> PollUntilAcquiredAsync(ILeadershipLeaseProvider node, Stopwatch clock)
    {
        var lease = await PollForLeaseAsync(node);
        if (lease is null)
        {
            return null;
        }

        var at = clock.Elapsed;
        await lease.DisposeAsync();
        return at;
    }

    private static async Task<ILeadershipLease?> PollForLeaseAsync(
        ILeadershipLeaseProvider node,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? Timeout);

        while (DateTime.UtcNow < deadline)
        {
            var lease = await node.TryAcquireLeadershipAsync();
            if (lease is not null)
            {
                return lease;
            }

            await Task.Delay(250);
        }

        return null;
    }
}
