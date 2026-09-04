using Consul;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;
using DLeader.Consul.Configuration;
using DLeader.Consul.Abstractions;
using DLeader.Consul.Diagnostics;
using DLeader.Consul.Exceptions;
using Microsoft.Extensions.Options;

namespace DLeader.Consul.Implementations;

/// <summary>
/// Distributed leader election using Consul sessions and a KV lock.
/// </summary>
/// <remarks>
/// <para>
/// Leadership is expressed as a lease: a claim that stays valid for the duration of the
/// caller's work, carries a fencing token to pass to the resource being protected, and
/// signals its own loss through a cancellation token. See
/// <see cref="ILeadershipLease"/> for what that does and does not guarantee.
/// </para>
/// <para>
/// Before 2.0 this type also carried a second, event-driven API over the same lock key,
/// with different guarantees and a mode guard to stop the two competing with each other.
/// That path is gone: one way of doing this means one set of guarantees to state and
/// keep. <c>LeaderElectedService</c> covers the ergonomics the events provided, built on
/// this type rather than beside it.
/// </para>
/// </remarks>
public class ConsulLeaderElection : ILeadershipLeaseProvider, IDisposable, IAsyncDisposable
{
    private readonly ILogger<ConsulLeaderElection> _logger;
    private readonly ConsulOptions _options;
    private readonly IConsulClient _consulClient;
    private readonly bool _ownsClient;
    private readonly string _instanceId;
    private readonly string _lockKey;
    private bool _disposed;

    /// <summary>
    /// Identifies this instance in the lock key's value and in Consul session names.
    /// Purely for diagnostics — nothing depends on it for correctness.
    /// </summary>
    public string InstanceId => _instanceId;

    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="options">Consul configuration.</param>
    /// <param name="consulClient">
    /// Consul client. When omitted, one is built from <paramref name="options"/> and
    /// disposed with this instance; when supplied, it is left alone, since it is
    /// normally a shared singleton.
    /// </param>
    public ConsulLeaderElection(
        ILogger<ConsulLeaderElection> logger,
        IOptions<ConsulOptions> options,
        IConsulClient? consulClient = null)
    {
        _logger = logger;
        _options = options.Value;

        var hostname = Environment.GetEnvironmentVariable("HOSTNAME") ?? Environment.MachineName;

        // The trailing suffix is not decoration. This value names the Consul sessions
        // this instance creates, so two instances sharing an id would be
        // indistinguishable in Consul's session list — and two instances in one process
        // is exactly what the tests construct. Host and process id stay in front so the
        // value is still readable in a log.
        _instanceId =
            $"{_options.ServiceName}-{hostname}-{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..8]}";

        _lockKey = $"service/{_options.ServiceName}/leader";

        _ownsClient = consulClient is null;
        _consulClient = consulClient ?? ConsulClientFactory.Create(_options);
    }

    /// <inheritdoc />
    public async Task<ILeadershipLease?> TryAcquireLeadershipAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var ttl = TimeSpan.FromSeconds(_options.SessionTTL);
        var safetyMargin = TimeSpan.FromSeconds(_options.LeaseSafetyMarginSeconds);

        if (safetyMargin <= TimeSpan.Zero || safetyMargin >= ttl)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ConsulOptions.LeaseSafetyMarginSeconds),
                _options.LeaseSafetyMarginSeconds,
                $"LeaseSafetyMarginSeconds must be greater than 0 and less than SessionTTL ({_options.SessionTTL}).");
        }

        string? sessionId = null;

        using var activity = LeadershipTelemetry.ActivitySource.StartActivity(
            "TryAcquireLeadership", ActivityKind.Client);
        activity?.SetTag("dleader.service", _options.ServiceName);
        activity?.SetTag("dleader.instance_id", _instanceId);

        try
        {
            sessionId = await CreateLeaseSessionAsync(ttl, cancellationToken);

            var pair = new KVPair(_lockKey)
            {
                Session = sessionId,
                Value = Encoding.UTF8.GetBytes(_instanceId)
            };

            var acquired = await _consulClient.KV.Acquire(pair, cancellationToken);
            if (!acquired.Response)
            {
                // Either someone else holds it, or Consul's lock delay from a previous
                // holder's failure has not elapsed yet. Both are ordinary outcomes.
                await DestroySessionQuietlyAsync(sessionId);

                activity?.SetTag("dleader.outcome", "contended");
                RecordAcquisition("contended");
                return null;
            }

            // Acquire returns only a boolean, so the fencing token has to come from a
            // follow-up read. Requiring Session to match ours turns that read into
            // proof rather than a guess: if Consul says this key is held by our
            // session, then we held it at the index it reports. A consistent read
            // keeps a stale follower from handing back an older index.
            var confirmation = await _consulClient.KV.Get(
                _lockKey,
                new QueryOptions { Consistency = ConsistencyMode.Consistent },
                cancellationToken);

            if (confirmation.Response is null ||
                !string.Equals(confirmation.Response.Session, sessionId, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "Acquired lock {LockKey} but confirmation shows it held by '{Holder}'; standing down",
                    _lockKey, confirmation.Response?.Session ?? "nobody");

                await DestroySessionQuietlyAsync(sessionId);

                activity?.SetTag("dleader.outcome", "contended");
                RecordAcquisition("contended");
                return null;
            }

            var modifyIndex = confirmation.Response.ModifyIndex;
            if (modifyIndex > long.MaxValue)
            {
                throw new LeadershipException(
                    $"Consul ModifyIndex {modifyIndex} exceeds the range of a fencing token.");
            }

            var lease = new ConsulLeadershipLease(
                _consulClient,
                _logger,
                _lockKey,
                _instanceId,
                sessionId,
                _options.ServiceName,
                (long)modifyIndex,
                modifyIndex,
                ttl,
                safetyMargin,
                cancellationToken);

            _logger.LogInformation(
                "Acquired leadership lease for {InstanceId} with fencing token {FencingToken}",
                _instanceId, lease.FencingToken);

            activity?.SetTag("dleader.outcome", "acquired");
            activity?.SetTag("dleader.fencing_token", lease.FencingToken);

            RecordAcquisition("acquired");

            LeadershipTelemetry.Held.Add(1,
                new KeyValuePair<string, object?>("service", _options.ServiceName));

            // Emitted so a monitoring system can alert on the token going backwards,
            // which would mean the Consul cluster was restored or rebuilt and the
            // assumption fencing rests on no longer holds.
            LeadershipTelemetry.FencingTokenIssued.Add(lease.FencingToken,
                new KeyValuePair<string, object?>("service", _options.ServiceName));

            return lease;
        }
        catch (Exception ex)
        {
            if (sessionId is not null)
            {
                await DestroySessionQuietlyAsync(sessionId);
            }

            if (ex is OperationCanceledException || ex is LeadershipException || ex is ArgumentOutOfRangeException)
            {
                throw;
            }

            _logger.LogError(ex, "Error acquiring leadership lease for {InstanceId}", _instanceId);

            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            RecordAcquisition("failed");

            throw new LeadershipException("Failed to acquire leadership lease", ex);
        }
    }

    /// <inheritdoc />
    public async Task<ILeadershipLease> AcquireLeadershipAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var lease = await TryAcquireLeadershipAsync(cancellationToken);
            if (lease is not null)
            {
                return lease;
            }

            await WaitForLockKeyToChangeAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Reports which instance currently holds the lock, for diagnostics.
    /// </summary>
    /// <returns>
    /// The holder's instance id, or an empty string when nobody holds it.
    /// </returns>
    /// <remarks>
    /// Advisory only, and stale the moment it returns. Never branch on it — that is the
    /// check-then-act race the lease API exists to remove. Acquire a lease instead.
    /// </remarks>
    public async Task<string> GetCurrentLeaderAsync()
    {
        ThrowIfDisposed();
        try
        {
            var pair = await _consulClient.KV.Get(_lockKey, CancellationToken.None);
            if (pair.Response is null)
                return string.Empty;

            // A key with no session attached is a leftover from a leader that failed or
            // released, not a leader. Lease sessions use Release behaviour, so the old
            // value stays in place and reporting it without this check would name an
            // instance that is no longer leading.
            if (string.IsNullOrEmpty(pair.Response.Session))
                return string.Empty;

            // Consul represents an empty value as a null byte array.
            if (pair.Response.Value is null)
                return string.Empty;

            return Encoding.UTF8.GetString(pair.Response.Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting current leader");
            throw new ConsulException("Failed to get current leader", ex);
        }
    }

    /// <summary>
    /// Blocks until the lock key changes, or until the lock delay could plausibly have
    /// elapsed, whichever comes first.
    /// </summary>
    /// <remarks>
    /// A blocking query means a follower takes over the moment the leader releases,
    /// instead of up to one poll interval later. The timeout matters as much as the
    /// query: after the holder fails, Consul refuses the lock for the lock-delay window
    /// without the key changing at all, so waiting only on a change would sit there
    /// until the query timed out. Waking around the end of that window retries at
    /// roughly the first moment acquisition can succeed.
    /// </remarks>
    private async Task WaitForLockKeyToChangeAsync(CancellationToken cancellationToken)
    {
        var waitTime = TimeSpan.FromSeconds(Math.Clamp(_options.LockDelaySeconds + 1, 1, 60));

        try
        {
            var current = await _consulClient.KV.Get(_lockKey, cancellationToken);

            var options = new QueryOptions
            {
                WaitIndex = current.LastIndex,
                WaitTime = waitTime
            };

            await _consulClient.KV.Get(_lockKey, options, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Consul being unreachable is not a reason to spin.
            _logger.LogDebug(ex, "Waiting on lock key {LockKey} failed; backing off", _lockKey);
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    /// <summary>
    /// Creates the session backing a lease.
    /// </summary>
    /// <remarks>
    /// Uses <c>Release</c> rather than <c>Delete</c> behaviour so that the lock key
    /// survives session invalidation. Consul attaches the lock delay to that surviving
    /// key, and the delay is what gives a failed leader time to notice its own loss
    /// before a successor can take over. A key that is deleted on invalidation leaves
    /// nothing for the delay to apply to.
    /// </remarks>
    private async Task<string> CreateLeaseSessionAsync(TimeSpan ttl, CancellationToken cancellationToken)
    {
        var sessionEntry = new SessionEntry
        {
            Name = _instanceId,
            TTL = ttl,
            LockDelay = TimeSpan.FromSeconds(_options.LockDelaySeconds),
            Behavior = SessionBehavior.Release
        };

        var result = await _consulClient.Session.Create(sessionEntry, cancellationToken);
        return result.Response;
    }

    private void RecordAcquisition(string outcome) =>
        LeadershipTelemetry.Acquisitions.Add(1,
            new KeyValuePair<string, object?>("service", _options.ServiceName),
            new KeyValuePair<string, object?>("outcome", outcome));

    private async Task DestroySessionQuietlyAsync(string sessionId)
    {
        try
        {
            await _consulClient.Session.Destroy(sessionId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to destroy unused session {SessionId}", sessionId);
        }
    }

    /// <exception cref="ObjectDisposedException">Thrown if the instance has been disposed.</exception>
    protected virtual void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <summary>
    /// Releases the resources used by this instance.
    /// </summary>
    /// <remarks>
    /// This type holds no leases of its own — each lease owns its session and releases
    /// it on disposal — so there is nothing here that can block. Dispose any leases you
    /// are holding before disposing this.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_ownsClient)
        {
            _consulClient.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
