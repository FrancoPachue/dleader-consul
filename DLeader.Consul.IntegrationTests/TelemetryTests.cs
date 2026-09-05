using System.Diagnostics.Metrics;
using DLeader.Consul.Abstractions;
using DLeader.Consul.Diagnostics;

namespace DLeader.Consul.IntegrationTests;

/// <summary>
/// That the metrics report the right reason, not just that they fire.
/// </summary>
/// <remarks>
/// The value of the <c>reason</c> tag is the whole point of this instrumentation: a
/// leader that stood down because Consul expired its session is a different incident
/// from one that stood down because it could not reach Consul. A test that only checked
/// "a loss was counted" would pass with the two swapped, and would have been worse than
/// no test — it would have made a wrong dashboard look verified.
/// </remarks>
public class TelemetryTests
{
    /// <summary>Collects one instrument into a list, for the duration of the using block.</summary>
    private sealed class Recorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<(long Value, Dictionary<string, object?> Tags)> _measurements = new();
        private readonly object _gate = new();

        public Recorder(string instrumentName)
        {
            _listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == LeadershipTelemetry.MeterName &&
                    instrument.Name == instrumentName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };

            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                var copy = new Dictionary<string, object?>();
                foreach (var tag in tags)
                {
                    copy[tag.Key] = tag.Value;
                }

                lock (_gate)
                {
                    _measurements.Add((value, copy));
                }
            });

            _listener.Start();
        }

        public IReadOnlyList<(long Value, Dictionary<string, object?> Tags)> Measurements
        {
            get { lock (_gate) { return _measurements.ToList(); } }
        }

        public void Dispose() => _listener.Dispose();
    }

    [Fact]
    public async Task LosingTheSession_ReportsSessionExpired_NotTheLocalDeadline()
    {
        var consul = new ConsulContainer();
        await consul.InitializeAsync();

        try
        {
            using var losses = new Recorder("dleader.consul.leadership.lost");

            var serviceName = ConsulContainer.NewServiceName();
            await using var node = consul.CreateNode(serviceName, servicePort: 5001);

            var lease = await node.TryAcquireLeadershipAsync();
            Assert.NotNull(lease);

            // Consul is up and reachable; it simply decides the session is gone.
            using (var client = consul.CreateClient())
            {
                var sessions = await client.Session.List();
                var target = sessions.Response.Single(s => s.Name == node.InstanceId);
                await client.Session.Destroy(target.ID);
            }

            await WaitUntilAsync(
                () => lease!.LostToken.IsCancellationRequested,
                TimeSpan.FromSeconds(30),
                "the lease never noticed the session was destroyed");

            // Consul told us, so it must not be attributed to the local deadline.
            Assert.Equal(LeadershipLostReason.LockKeyTaken, lease!.LostReason);

            var reasons = losses.Measurements
                .Where(m => Equals(m.Tags.GetValueOrDefault("service"), serviceName))
                .Select(m => m.Tags.GetValueOrDefault("reason") as string)
                .ToList();

            Assert.Contains("lock_key_taken", reasons);
            Assert.DoesNotContain("local_deadline_exceeded", reasons);

            await lease.DisposeAsync();
        }
        finally
        {
            await consul.DisposeAsync();
        }
    }

    [Fact]
    public async Task ConsulBecomingUnreachable_ReportsTheLocalDeadline_NotSessionExpired()
    {
        var consul = new ConsulContainer();
        await consul.InitializeAsync();

        try
        {
            using var losses = new Recorder("dleader.consul.leadership.lost");

            var serviceName = ConsulContainer.NewServiceName();
            await using var node = consul.CreateNode(
                serviceName, servicePort: 5001, sessionTtlSeconds: 10, safetyMarginSeconds: 2);

            var lease = await node.TryAcquireLeadershipAsync();
            Assert.NotNull(lease);

            // Nobody tells us anything: the agent is simply gone.
            await consul.StopAgentAsync();

            await WaitUntilAsync(
                () => lease!.LostToken.IsCancellationRequested,
                TimeSpan.FromSeconds(45),
                "the lease never gave up after Consul became unreachable");

            // This is the distinction that matters: the node decided, not Consul.
            Assert.Equal(LeadershipLostReason.LocalDeadlineExceeded, lease!.LostReason);

            var reasons = losses.Measurements
                .Where(m => Equals(m.Tags.GetValueOrDefault("service"), serviceName))
                .Select(m => m.Tags.GetValueOrDefault("reason") as string)
                .ToList();

            Assert.Contains("local_deadline_exceeded", reasons);
            Assert.DoesNotContain("session_expired", reasons);
        }
        finally
        {
            await consul.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposingCleanly_ReportsReleased_AndBalancesTheHeldGauge()
    {
        var consul = new ConsulContainer();
        await consul.InitializeAsync();

        try
        {
            using var held = new Recorder("dleader.consul.leadership.held");

            var serviceName = ConsulContainer.NewServiceName();
            await using var node = consul.CreateNode(serviceName, servicePort: 5001);

            var lease = await node.TryAcquireLeadershipAsync();
            Assert.NotNull(lease);
            await lease!.DisposeAsync();

            Assert.Equal(LeadershipLostReason.Released, lease.LostReason);

            // A gauge that only ever increments is how you end up with a dashboard
            // claiming six leaders for a service that has one.
            var net = held.Measurements
                .Where(m => Equals(m.Tags.GetValueOrDefault("service"), serviceName))
                .Sum(m => m.Value);

            Assert.Equal(0, net);
        }
        finally
        {
            await consul.DisposeAsync();
        }
    }

    /// <summary>
    /// The watchdog fires at TTL minus the safety margin: 8 seconds by default. Renewing
    /// at half the TTL left exactly one attempt inside that window with three seconds
    /// of headroom, so a renewal Consul accepted but answered slowly still lost the
    /// lease. Renewing at a third puts two attempts inside the window. This pins that
    /// down through the renewal metric rather than by timing a slow Consul, which
    /// would be a flake.
    /// </summary>
    [Fact]
    public async Task AHeldLease_RenewsAtLeastTwice_BeforeTheLocalDeadlineWouldFire()
    {
        var consul = new ConsulContainer();
        await consul.InitializeAsync();

        try
        {
            using var renewals = new Recorder("dleader.consul.session.renewals");

            var serviceName = ConsulContainer.NewServiceName();
            await using var node = consul.CreateNode(
                serviceName, servicePort: 5001, sessionTtlSeconds: 10, safetyMarginSeconds: 2);

            await using var lease = await node.TryAcquireLeadershipAsync();
            Assert.NotNull(lease);

            // Just past the 8-second deadline. With renewal at TTL/3 there have been
            // two by now (3.3s, 6.7s); at TTL/2 there would have been one (5s).
            await Task.Delay(TimeSpan.FromSeconds(8.5));

            Assert.False(lease!.LostToken.IsCancellationRequested,
                "a healthy lease against a healthy agent was declared lost");

            var successful = renewals.Measurements
                .Where(m => Equals(m.Tags.GetValueOrDefault("service"), serviceName))
                .Where(m => Equals(m.Tags.GetValueOrDefault("outcome"), "ok"))
                .Sum(m => m.Value);

            Assert.True(successful >= 2,
                $"expected at least two successful renewals inside the deadline window, saw {successful}");
        }
        finally
        {
            await consul.DisposeAsync();
        }
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
