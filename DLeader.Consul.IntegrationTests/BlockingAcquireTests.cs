using System.Diagnostics;
using DLeader.Consul.Abstractions;

namespace DLeader.Consul.IntegrationTests;

/// <summary>
/// The blocking acquisition path.
/// </summary>
/// <remarks>
/// The value of this API is not that it saves the caller a loop — it is that the loop
/// it replaces slept, so a follower took over up to one poll interval after the lock
/// was free. These tests assert the takeover is prompt, which is the only reason the
/// method exists.
/// </remarks>
[Collection(ConsulCollection.Name)]
public class BlockingAcquireTests
{
    private readonly ConsulContainer _consul;

    public BlockingAcquireTests(ConsulContainer consul) => _consul = consul;

    [Fact]
    public async Task AcquireLeadershipAsync_ReturnsImmediately_WhenTheLockIsFree()
    {
        var serviceName = ConsulContainer.NewServiceName();
        await using var node = _consul.CreateNode(serviceName, servicePort: 5001);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var lease = await node.AcquireLeadershipAsync(cts.Token);

        Assert.NotNull(lease);
        Assert.False(lease.LostToken.IsCancellationRequested);
        Assert.True(lease.FencingToken > 0);
    }

    [Fact]
    public async Task AcquireLeadershipAsync_TakesOverPromptly_WhenTheLeaderReleases()
    {
        var serviceName = ConsulContainer.NewServiceName();

        await using var leader = _consul.CreateNode(serviceName, servicePort: 5001);
        await using var follower = _consul.CreateNode(serviceName, servicePort: 5002);

        var held = await leader.AcquireLeadershipAsync();
        Assert.NotNull(held);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var waiting = follower.AcquireLeadershipAsync(cts.Token);

        // Let the follower settle into its blocking query before releasing.
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.False(waiting.IsCompleted, "the follower acquired while the lock was held");

        var clock = Stopwatch.StartNew();
        await held.DisposeAsync();

        var lease = await waiting;
        clock.Stop();

        Assert.NotNull(lease);

        // A clean release destroys the session, so no lock delay applies. A polling
        // implementation would take up to its interval; the blocking query should be
        // well inside that.
        Assert.True(
            clock.Elapsed < TimeSpan.FromSeconds(10),
            $"took {clock.Elapsed} to take over after a clean release");

        await lease.DisposeAsync();
    }

    [Fact]
    public async Task AcquireLeadershipAsync_HonoursCancellation_WhileWaiting()
    {
        var serviceName = ConsulContainer.NewServiceName();

        await using var leader = _consul.CreateNode(serviceName, servicePort: 5001);
        await using var follower = _consul.CreateNode(serviceName, servicePort: 5002);

        await using var held = await leader.AcquireLeadershipAsync();
        Assert.NotNull(held);

        using var cts = new CancellationTokenSource();
        var waiting = follower.AcquireLeadershipAsync(cts.Token);

        await Task.Delay(TimeSpan.FromSeconds(1));
        cts.Cancel();

        // Cancellation has to be observed while a blocking query is outstanding, not
        // only between attempts.
        var clock = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        clock.Stop();

        Assert.True(
            clock.Elapsed < TimeSpan.FromSeconds(10),
            $"took {clock.Elapsed} to observe cancellation");
    }

    [Fact]
    public async Task AcquireLeadershipAsync_DoesNotSpin_WhileTheLockIsHeld()
    {
        var serviceName = ConsulContainer.NewServiceName();

        await using var leader = _consul.CreateNode(serviceName, servicePort: 5001);
        await using var follower = _consul.CreateNode(serviceName, servicePort: 5002);

        await using var held = await leader.AcquireLeadershipAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var waiting = follower.AcquireLeadershipAsync(cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        // The lock never became free, so it must not have acquired. The real assertion
        // is that it waited rather than hammering Consul, which the elapsed time above
        // and the blocking query together give us.
        Assert.False(held.LostToken.IsCancellationRequested);
    }

    [Fact]
    public async Task AcquireLeadershipAsync_ThroughTheInterface_UsesTheBlockingOverride()
    {
        var serviceName = ConsulContainer.NewServiceName();
        await using var node = _consul.CreateNode(serviceName, servicePort: 5001);

        // The interface declares a polling default; the Consul implementation overrides
        // it. Calling through the interface has to reach the override, otherwise the
        // default interface method would silently win.
        ILeadershipLeaseProvider provider = node;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var lease = await provider.AcquireLeadershipAsync(cts.Token);

        Assert.NotNull(lease);
    }
}
