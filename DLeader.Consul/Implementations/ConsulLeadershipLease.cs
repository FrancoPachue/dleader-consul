using Consul;
using DLeader.Consul.Abstractions;
using DLeader.Consul.Diagnostics;
using Microsoft.Extensions.Logging;
using System.Text;

namespace DLeader.Consul.Implementations;

/// <summary>
/// A held leadership lease backed by a dedicated Consul session.
/// </summary>
/// <remarks>
/// Three independent loops run for the lifetime of the lease and any of them can
/// declare it lost: a renewal loop that keeps the session alive, a watchdog that
/// enforces a local deadline on a monotonic clock, and a blocking-query watch on the
/// lock key. The watchdog is the one that matters for safety - it depends on nothing
/// but the local clock, so a partition that silences Consul entirely still causes the
/// lease to give up on schedule.
/// </remarks>
internal sealed class ConsulLeadershipLease : ILeadershipLease
{
    private readonly IConsulClient _consulClient;
    private readonly ILogger _logger;
    private readonly string _lockKey;
    private readonly string _instanceId;
    private readonly string _sessionId;
    private readonly TimeSpan _ttl;
    private readonly TimeSpan _localDeadline;
    private readonly CancellationTokenSource _lostCts;

    /// <summary>
    /// Captured at construction so that the property keeps working after disposal.
    /// Reading <c>CancellationTokenSource.Token</c> throws once the source is disposed,
    /// and a caller checking whether it still holds leadership right after releasing
    /// the lease is asking a reasonable question that deserves <c>true</c> rather than
    /// an exception.
    /// </summary>
    private readonly CancellationToken _lostToken;

    /// <summary>Monotonic timestamp of the last successful session renewal.</summary>
    private long _lastRenewalTicks;

    /// <summary>Monotonic timestamp of acquisition, for the tenure histogram.</summary>
    private readonly long _acquiredAtTicks = Environment.TickCount64;

    /// <summary>
    /// The loss reason, stored as the enum value plus one so that 0 means "still held".
    /// An int because three loops race to set it and only the first should win.
    /// </summary>
    private int _lostReason;

    /// <summary>Service name, carried purely as a metric tag.</summary>
    private readonly string _serviceName;

    private Task? _renewalLoop;
    private Task? _watchdogLoop;
    private Task? _watchLoop;
    private int _disposed;

    /// <inheritdoc />
    public CancellationToken LostToken => _lostToken;

    /// <inheritdoc />
    public long FencingToken { get; }

    /// <inheritdoc />
    public LeadershipLostReason? LostReason
    {
        get
        {
            var stored = Volatile.Read(ref _lostReason);
            return stored == 0 ? null : (LeadershipLostReason)(stored - 1);
        }
    }

    internal ConsulLeadershipLease(
        IConsulClient consulClient,
        ILogger logger,
        string lockKey,
        string instanceId,
        string sessionId,
        string serviceName,
        long fencingToken,
        ulong acquiredAtIndex,
        TimeSpan ttl,
        TimeSpan safetyMargin,
        CancellationToken callerToken)
    {
        _consulClient = consulClient;
        _logger = logger;
        _lockKey = lockKey;
        _instanceId = instanceId;
        _sessionId = sessionId;
        _serviceName = serviceName;
        _ttl = ttl;
        FencingToken = fencingToken;

        // The deadline is measured from the last renewal, so it has to be shorter than
        // the TTL by enough to cover a renewal round trip that Consul may already have
        // given up waiting for.
        _localDeadline = ttl - safetyMargin;
        if (_localDeadline <= TimeSpan.Zero)
        {
            _localDeadline = TimeSpan.FromMilliseconds(ttl.TotalMilliseconds / 2);
        }

        // Linking means host shutdown, or any other cancellation the caller controls,
        // surfaces to consumers as loss of leadership rather than as silence.
        _lostCts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        _lostToken = _lostCts.Token;
        _lastRenewalTicks = Environment.TickCount64;

        _renewalLoop = RenewalLoopAsync();
        _watchdogLoop = WatchdogLoopAsync();
        _watchLoop = WatchLoopAsync(acquiredAtIndex);
    }

    /// <summary>
    /// Keeps the Consul session alive. Renews at half the TTL, and backs off to a short
    /// retry after a transient failure so that one failed round trip does not consume
    /// the whole margin.
    /// </summary>
    private async Task RenewalLoopAsync()
    {
        var token = _lostCts.Token;
        var normalInterval = TimeSpan.FromMilliseconds(_ttl.TotalMilliseconds / 2);
        var retryInterval = TimeSpan.FromMilliseconds(Math.Max(250, _ttl.TotalMilliseconds / 10));
        var delay = normalInterval;

        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
                await _consulClient.Session.Renew(_sessionId, token).ConfigureAwait(false);

                Interlocked.Exchange(ref _lastRenewalTicks, Environment.TickCount64);
                delay = normalInterval;

                LeadershipTelemetry.Renewals.Add(1,
                    new KeyValuePair<string, object?>("service", _serviceName),
                    new KeyValuePair<string, object?>("outcome", "ok"));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SessionExpiredException ex)
            {
                MarkLost(LeadershipLostReason.SessionExpired, "Consul reports the session as expired: " + ex.Message);
                break;
            }
            catch (ConsulRequestException ex) when (IsSessionGone(ex))
            {
                MarkLost(LeadershipLostReason.SessionExpired, "Consul rejected the renewal as an invalid session: " + ex.Message);
                break;
            }
            catch (Exception ex)
            {
                // Could be transient. Retry sooner than the normal interval and let the
                // watchdog decide when the margin has actually run out.
                _logger.LogWarning(
                    ex,
                    "Session renewal failed for lease {SessionId} on {InstanceId}; retrying in {Delay}",
                    _sessionId, _instanceId, retryInterval);
                delay = retryInterval;

                // A rising failure count with no corresponding loss is the early warning
                // that the margin is being eaten into.
                LeadershipTelemetry.Renewals.Add(1,
                    new KeyValuePair<string, object?>("service", _serviceName),
                    new KeyValuePair<string, object?>("outcome", "failed"));
            }
        }
    }

    /// <summary>
    /// Enforces the local deadline. This is the only loss detector that keeps working
    /// when Consul is unreachable, which is exactly when it matters.
    /// </summary>
    private async Task WatchdogLoopAsync()
    {
        var token = _lostCts.Token;
        var tick = TimeSpan.FromMilliseconds(Math.Max(100, _localDeadline.TotalMilliseconds / 10));

        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(tick, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // Environment.TickCount64 is monotonic, so wall-clock adjustments and NTP
            // slew cannot move this deadline.
            var sinceRenewal = Environment.TickCount64 - Interlocked.Read(ref _lastRenewalTicks);
            if (sinceRenewal >= (long)_localDeadline.TotalMilliseconds)
            {
                MarkLost(
                    LeadershipLostReason.LocalDeadlineExceeded,
                    "no session renewal succeeded in " + sinceRenewal +
                    " ms, which exceeds the local deadline of " +
                    _localDeadline.TotalMilliseconds + " ms");
                break;
            }
        }
    }

    /// <summary>
    /// Watches the lock key with a blocking query so that losing the lock to another
    /// session is noticed promptly rather than at the next deadline.
    /// </summary>
    private async Task WatchLoopAsync(ulong startIndex)
    {
        var token = _lostCts.Token;
        var index = startIndex;

        while (!token.IsCancellationRequested)
        {
            try
            {
                var options = new QueryOptions
                {
                    WaitIndex = index,
                    WaitTime = TimeSpan.FromSeconds(Math.Max(10, _ttl.TotalSeconds))
                };

                var result = await _consulClient.KV.Get(_lockKey, options, token).ConfigureAwait(false);

                // Consul's blocking-query contract: an index that moves backwards means
                // the state was reset, and the client must restart from zero.
                index = result.LastIndex < index ? 0 : result.LastIndex;

                if (result.Response is null)
                {
                    MarkLost(LeadershipLostReason.LockKeyTaken, "the lock key no longer exists");
                    break;
                }

                if (!string.Equals(result.Response.Session, _sessionId, StringComparison.Ordinal))
                {
                    MarkLost(
                        LeadershipLostReason.LockKeyTaken,
                        "the lock key is no longer held by this session (now held by " +
                        (result.Response.Session ?? "nobody") + ")");
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failing watch is not evidence of loss; the watchdog owns that call.
                _logger.LogDebug(
                    ex,
                    "Watch on lock key {LockKey} failed for lease {SessionId}; retrying",
                    _lockKey, _sessionId);

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private void MarkLost(LeadershipLostReason reason, string detail)
    {
        // Only the first detector to fire owns the reason. Three loops race to call
        // this, and the one that got there first is the one that actually decided.
        if (Interlocked.CompareExchange(ref _lostReason, (int)reason + 1, 0) != 0)
        {
            return;
        }

        _logger.LogWarning(
            "Leadership lease lost by {InstanceId} (fencing token {FencingToken}, reason {Reason}): {Detail}",
            _instanceId, FencingToken, reason, detail);

        LeadershipTelemetry.Losses.Add(1,
            new KeyValuePair<string, object?>("service", _serviceName),
            new KeyValuePair<string, object?>("reason", ReasonTag(reason)));

        LeadershipTelemetry.Held.Add(-1,
            new KeyValuePair<string, object?>("service", _serviceName));

        LeadershipTelemetry.Tenure.Record(
            (Environment.TickCount64 - _acquiredAtTicks) / 1000.0,
            new KeyValuePair<string, object?>("service", _serviceName),
            new KeyValuePair<string, object?>("reason", ReasonTag(reason)));

        try
        {
            _lostCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Raced with disposal; the lease is going away anyway.
        }
    }

    /// <summary>
    /// Stable, low-cardinality tag values. The enum names would work, but pinning them
    /// here means renaming the enum cannot silently break someone's dashboard.
    /// </summary>
    private static string ReasonTag(LeadershipLostReason reason) => reason switch
    {
        LeadershipLostReason.SessionExpired => "session_expired",
        LeadershipLostReason.LocalDeadlineExceeded => "local_deadline_exceeded",
        LeadershipLostReason.LockKeyTaken => "lock_key_taken",
        LeadershipLostReason.Released => "released",
        LeadershipLostReason.Cancelled => "cancelled",
        _ => "unknown"
    };

    /// <summary>
    /// True when the exception is Consul rejecting an operation because the session no
    /// longer exists. Consul answers with HTTP 500 and a message rather than a distinct
    /// status code, so the message is the only signal available.
    /// </summary>
    internal static bool IsSessionGone(ConsulRequestException ex) =>
        ex.Message.Contains("invalid session", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("session not found", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("Session id", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Records the term and decrements the held gauge, unless a detector already did.
        // Cancelled rather than Released when the caller's token went first, so the
        // metrics distinguish an orderly hand-off from a host shutting down.
        MarkLost(
            _lostToken.IsCancellationRequested ? LeadershipLostReason.Cancelled : LeadershipLostReason.Released,
            "the lease was disposed");

        try
        {
            _lostCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already gone.
        }

        foreach (var loop in new[] { _renewalLoop, _watchdogLoop, _watchLoop })
        {
            if (loop is null)
            {
                continue;
            }

            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background loop of lease {SessionId} faulted", _sessionId);
            }
        }

        _renewalLoop = _watchdogLoop = _watchLoop = null;

        // Release is scoped to our session: Consul ignores it when this session no
        // longer holds the key, so it can never take the lock away from a successor the
        // way an unconditional delete would.
        try
        {
            var released = await _consulClient.KV.Release(
                new KVPair(_lockKey)
                {
                    Session = _sessionId,
                    Value = Encoding.UTF8.GetBytes(_instanceId)
                }, CancellationToken.None).ConfigureAwait(false);

            _logger.LogInformation(
                "Released leadership lock {LockKey} held by {InstanceId} (accepted: {Accepted})",
                _lockKey, _instanceId, released.Response);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to release leadership lock {LockKey}", _lockKey);
        }

        // Destroying the session drops the lock now instead of leaving a successor to
        // wait out the TTL.
        try
        {
            await _consulClient.Session.Destroy(_sessionId, CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation("Destroyed lease session {SessionId}", _sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to destroy lease session {SessionId}", _sessionId);
        }

        _lostCts.Dispose();
    }
}
