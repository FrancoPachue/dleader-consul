using System.Diagnostics;

namespace DLeader.Consul.IntegrationTests;

/// <summary>
/// Covers the case the rest of the suite cannot: Consul becomes unreachable entirely.
/// </summary>
/// <remarks>
/// This is the test that matters most for safety. Every other loss detector needs a
/// working connection to Consul in order to hear that leadership moved, which means
/// none of them fire during the exact failure - a partition - where continuing to act
/// as leader is most dangerous. The watchdog runs off a monotonic local clock and owes
/// nothing to the network, so it is the one mechanism that still gives up on schedule.
/// It gets its own container because the test has to stop the agent.
/// </remarks>
public class LeaseWatchdogTests : IAsyncLifetime
{
    private readonly ConsulContainer _consul = new();

    public Task InitializeAsync() => _consul.InitializeAsync();

    public Task DisposeAsync() => _consul.DisposeAsync();

    [Fact]
    public async Task LostToken_IsCancelled_OnTheLocalDeadline_WhenConsulBecomesUnreachable()
    {
        const int ttlSeconds = 10;
        const int safetyMarginSeconds = 2;

        // The deadline is measured from the last successful renewal, so the worst case
        // is a renewal landing right before the agent stops: one renewal interval
        // (TTL/2) plus the deadline itself.
        var expectedDeadline = TimeSpan.FromSeconds(ttlSeconds - safetyMarginSeconds);
        var worstCase = expectedDeadline + TimeSpan.FromSeconds(ttlSeconds / 2.0);

        await using var node = _consul.CreateNode(
            ConsulContainer.NewServiceName(),
            servicePort: 5001,
            sessionTtlSeconds: ttlSeconds,
            safetyMarginSeconds: safetyMarginSeconds);

        var lease = await node.TryAcquireLeadershipAsync();
        Assert.NotNull(lease);
        Assert.False(lease!.LostToken.IsCancellationRequested);

        await _consul.StopAgentAsync();
        var clock = Stopwatch.StartNew();

        var cancelled = new TaskCompletionSource();
        using (lease.LostToken.Register(() => cancelled.TrySetResult()))
        {
            var finished = await Task.WhenAny(
                cancelled.Task,
                Task.Delay(worstCase + TimeSpan.FromSeconds(10)));

            Assert.True(
                ReferenceEquals(finished, cancelled.Task),
                $"the lease was still held {clock.Elapsed} after Consul became unreachable");
        }

        var elapsed = clock.Elapsed;

        // It must not give up early either: cancelling before the deadline would mean
        // surrendering leadership over a blip.
        Assert.True(
            elapsed >= expectedDeadline - TimeSpan.FromSeconds(2),
            $"gave up after only {elapsed}, sooner than the {expectedDeadline} deadline");

        Assert.True(
            elapsed <= worstCase + TimeSpan.FromSeconds(5),
            $"took {elapsed} to give up, beyond the worst case of {worstCase}");
    }
}
