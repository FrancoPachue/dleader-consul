using Consul;
using Microsoft.Extensions.Logging;
using System.Text;
using DLeader.Consul.Configuration;
using DLeader.Consul.Abstractions;
using DLeader.Consul.Exceptions;
using Microsoft.Extensions.Options;

namespace DLeader.Consul.Implementations;

/// <summary>
/// Distributed leader election implementation using Consul.
/// </summary>
/// <remarks>
/// Exposes two mutually exclusive modes over the same lock key. The lease API of
/// <see cref="ILeadershipLeaseProvider"/> is the safe one and is what new code should
/// use. The campaign API of <see cref="ILeaderElection"/> is kept for compatibility;
/// its <c>IsLeaderAsync</c> is obsolete because it cannot be used without a
/// check-then-act race.
/// </remarks>
public class ConsulLeaderElection :
    ILeaderElection,
    ILeadershipLeaseProvider,
    IDisposable,
    IAsyncDisposable
{
    private readonly ILogger<ConsulLeaderElection> _logger;
    private readonly ConsulOptions _options;
    private readonly ServiceRegistrationOptions _serviceOptions;
    private readonly IConsulClient _consulClient;
    private readonly string _instanceId;
    private readonly string _lockKey;
    private readonly CancellationTokenSource _cts;
    private bool _disposed;

    /// <summary>
    /// Written by the election loop and read from disposal on another thread, so it
    /// needs the volatile read/write barriers.
    /// </summary>
    private volatile bool _isLeader;

    // Background tasks tracking
    private Task? _electionTask;
    private Task? _sessionRenewalTask;

    /// <summary>
    /// Stops the background loops. Linked to the caller's token so that disposal can
    /// stop them even when the caller's token is never cancelled.
    /// </summary>
    private CancellationTokenSource? _electionCts;

    /// <summary>0 = untouched, 1 = campaign API in use, 2 = lease API in use.</summary>
    private int _mode;

    /// <summary>
    /// The session the campaign loop currently holds the lock with. Needed so that
    /// disposal can release the lock against that specific session instead of deleting
    /// the key outright.
    /// </summary>
    private volatile string? _currentCampaignSessionId;

    private const int ModeCampaign = 1;
    private const int ModeLease = 2;

    /// <summary>
    /// The maximum time the synchronous <see cref="Dispose"/> will block waiting for
    /// the asynchronous cleanup.
    /// </summary>
    private static readonly TimeSpan SyncDisposeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Event triggered when this instance becomes the leader
    /// </summary>
    public event Func<Task>? OnLeadershipAcquired;

    /// <summary>
    /// Event triggered when this instance loses leadership
    /// </summary>
    public event Func<Task>? OnLeadershipLost;

    /// <summary>
    /// Gets the unique identifier for this instance
    /// </summary>
    public string InstanceId => _instanceId;

    /// <summary>
    /// Constructor for ConsulLeaderElection
    /// </summary>
    /// <param name="logger">Logger for diagnostics</param>
    /// <param name="options">Consul configuration options</param>
    /// <param name="serviceOptions">Service registration options</param>
    /// <param name="consulClient">Optional Consul client</param>
    /// <exception cref="ArgumentNullException">If any required parameter is null</exception>
    public ConsulLeaderElection(
        ILogger<ConsulLeaderElection> logger,
        IOptions<ConsulOptions> options,
        IOptions<ServiceRegistrationOptions> serviceOptions,
        IConsulClient? consulClient = null)
    {
        _logger = logger;
        _options = options.Value;
        _serviceOptions = serviceOptions.Value;

        var hostname = Environment.GetEnvironmentVariable("HOSTNAME") ?? Environment.MachineName;
        _instanceId = $"{_options.ServiceName}-{hostname}-{_serviceOptions.ServicePort}";
        _lockKey = $"service/{_options.ServiceName}/leader";
        _cts = new CancellationTokenSource();

        _consulClient = consulClient ?? new ConsulClient(config =>
        {
            config.Address = new Uri(_options.Address);
        });
    }

    // ---------------------------------------------------------------------------
    // Lease API - the race-free path
    // ---------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<ILeadershipLease?> TryAcquireLeadershipAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnterMode(ModeLease);

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
                (long)modifyIndex,
                modifyIndex,
                ttl,
                safetyMargin,
                cancellationToken);

            _logger.LogInformation(
                "Acquired leadership lease for {InstanceId} with fencing token {FencingToken}",
                _instanceId, lease.FencingToken);

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
            throw new LeadershipException("Failed to acquire leadership lease", ex);
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

    /// <summary>
    /// Claims one of the two mutually exclusive modes.
    /// </summary>
    /// <remarks>
    /// Both modes contend for the same lock key with different sessions, so an
    /// instance running both would compete with itself and could hand leadership back
    /// and forth between its own two sessions. Failing loudly is better than
    /// debugging that.
    /// </remarks>
    private void EnterMode(int mode)
    {
        var previous = Interlocked.CompareExchange(ref _mode, mode, 0);
        if (previous != 0 && previous != mode)
        {
            throw new InvalidOperationException(
                "A single ConsulLeaderElection instance cannot use both the campaign API " +
                "(StartLeaderElectionAsync) and the lease API (TryAcquireLeadershipAsync): " +
                "they contend for the same lock key with separate sessions. Pick one, or " +
                "register a separate instance per mode.");
        }
    }

    // ---------------------------------------------------------------------------
    // Campaign API - kept for compatibility
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Starts the leader election campaign
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to stop the election process</param>
    /// <exception cref="LeadershipException">Thrown when the election process fails to start</exception>
    public async Task StartLeaderElectionAsync(CancellationToken cancellationToken)
    {
        EnterMode(ModeCampaign);

        try
        {
            // Linking to _cts means disposal stops the loops even when the caller's
            // token is never cancelled. Without it, disposal would wait forever on
            // loops nothing had told to stop.
            _electionCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
            var token = _electionCts.Token;

            await DeregisterPreviousServiceAsync(token);
            await RegisterServiceAsync(token);
            await VerifyServiceRegistrationAsync(token);

            var sessionId = await CreateSessionAsync(token);
            _logger.LogInformation("Created Consul session: {SessionId}", sessionId);

            _electionTask = RunLeaderElectionAsync(sessionId, token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting leader election");
            throw new LeadershipException("Failed to start leadership election", ex);
        }
    }

    /// <summary>
    /// Deregisters any previous instances of the service
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    private async Task DeregisterPreviousServiceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var services = await _consulClient.Agent.Services(cancellationToken);
            if (services?.Response != null)
            {
                var staleServices = services.Response
                    .Where(s => s.Value.Service == _options.ServiceName && s.Key != _instanceId)
                    .Select(s => s.Key);

                foreach (var serviceId in staleServices)
                {
                    try
                    {
                        _logger.LogInformation("Attempting to deregister stale service: {ServiceId}", serviceId);
                        await _consulClient.Agent.ServiceDeregister(serviceId, cancellationToken);
                        _logger.LogInformation("Successfully deregistered stale service: {ServiceId}", serviceId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to deregister stale service: {ServiceId}", serviceId);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during service cleanup");
        }
    }

    /// <summary>
    /// Registers this instance as a service in Consul
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    private async Task RegisterServiceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var registration = CreateServiceRegistration();

            _logger.LogInformation("Registering service with ID: {ServiceId}, Address: {Address}, Port: {Port}",
                _instanceId, registration.Address, registration.Port);

            await _consulClient.Agent.ServiceRegister(registration, cancellationToken);
            _logger.LogInformation("Service registered in Consul with ID: {ServiceId}", _instanceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register service: {ServiceId}", _instanceId);
            throw;
        }
    }

    /// <summary>
    /// Creates the service registration configuration
    /// </summary>
    /// <returns>The service registration configuration</returns>
    private AgentServiceRegistration CreateServiceRegistration()
    {
        return new AgentServiceRegistration
        {
            ID = _instanceId,
            Name = _options.ServiceName,
            Tags = new[] { "leadership-service" },
            Port = _serviceOptions.ServicePort,
            Address = GetHostAddress(),
            Check = new AgentServiceCheck
            {
                DeregisterCriticalServiceAfter = TimeSpan.FromMinutes(1),
                HTTP = $"http://{GetHostAddress()}:{_serviceOptions.ServicePort}/health",
                Interval = TimeSpan.FromSeconds(10),
                Timeout = TimeSpan.FromSeconds(5)
            }
        };
    }

    /// <summary>
    /// Verifies that the service was successfully registered in Consul
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    private async Task VerifyServiceRegistrationAsync(CancellationToken cancellationToken)
    {
        var retryCount = 0;

        while (retryCount < _options.VerificationRetries)
        {
            try
            {
                var services = await _consulClient.Agent.Services(cancellationToken);

                if (services?.Response == null)
                {
                    _logger.LogWarning("Consul returned null response when querying services");
                }
                else if (services.Response.TryGetValue(_instanceId, out var registeredService))
                {
                    _logger.LogInformation("Service registration verified. Found service with ID: {ServiceId}", _instanceId);
                    return;
                }
                else
                {
                    _logger.LogWarning("Service not found in verification attempt {Attempt}/{MaxAttempts}",
                        retryCount + 1, _options.VerificationRetries);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Error verifying service registration on attempt {Attempt}/{MaxAttempts}",
                    retryCount + 1, _options.VerificationRetries);
            }

            retryCount++;
            if (retryCount < _options.VerificationRetries)
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.VerificationRetryDelay), cancellationToken);
            }
        }

        _logger.LogWarning("Service verification did not succeed after {MaxAttempts} attempts, but continuing...",
            _options.VerificationRetries);
    }

    /// <summary>
    /// Creates a new session in Consul
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The session ID</returns>
    private async Task<string> CreateSessionAsync(CancellationToken cancellationToken)
    {
        var sessionEntry = new SessionEntry
        {
            Name = _instanceId,
            TTL = TimeSpan.FromSeconds(_options.SessionTTL),
            Behavior = SessionBehavior.Delete
        };

        var sessionId = (await _consulClient.Session.Create(sessionEntry, cancellationToken)).Response;
        _currentCampaignSessionId = sessionId;
        _sessionRenewalTask = RenewSessionAsync(sessionId, cancellationToken);
        return sessionId;
    }

    /// <summary>
    /// Runs the leader election loop
    /// </summary>
    /// <param name="sessionId">The session ID to use for leadership</param>
    /// <param name="cancellationToken">Cancellation token</param>
    private async Task RunLeaderElectionAsync(string sessionId, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var pair = new KVPair(_lockKey)
                {
                    Session = sessionId,
                    Value = Encoding.UTF8.GetBytes(_instanceId)
                };

                var acquiredLock = await _consulClient.KV.Acquire(pair, cancellationToken);

                if (acquiredLock.Response && !_isLeader)
                {
                    _isLeader = true;
                    await RaiseLeadershipAcquiredEvent();
                }
                else if (!acquiredLock.Response && _isLeader)
                {
                    _isLeader = false;
                    await RaiseLeadershipLostEvent();
                }

                await Task.Delay(TimeSpan.FromSeconds(_options.LeaderCheckInterval), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (IsSessionInvalid(ex))
            {
                // Consul answers an Acquire against a dead session with HTTP 500, which
                // surfaces as an exception rather than a false result. Without this
                // branch the loop never reaches the code that lowers _isLeader, so the
                // node keeps believing it leads while a successor already does, and it
                // never rebuilds its session so it can never lead again.
                _logger.LogWarning(ex,
                    "Consul session {SessionId} is no longer valid; standing down and recreating it",
                    sessionId);

                if (_isLeader)
                {
                    _isLeader = false;
                    await RaiseLeadershipLostEvent();
                }

                try
                {
                    sessionId = await RecreateSessionAsync(cancellationToken);
                    _logger.LogInformation("Recreated Consul session: {SessionId}", sessionId);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception recreateEx)
                {
                    _logger.LogError(recreateEx, "Failed to recreate Consul session; will retry");
                }

                await DelayQuietlyAsync(TimeSpan.FromSeconds(1), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in leader election loop");
                await DelayQuietlyAsync(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
    }

    /// <summary>
    /// Replaces the session used by the campaign loop after Consul invalidated it, and
    /// restarts the renewal loop that goes with it.
    /// </summary>
    private async Task<string> RecreateSessionAsync(CancellationToken cancellationToken)
    {
        var sessionEntry = new SessionEntry
        {
            Name = _instanceId,
            TTL = TimeSpan.FromSeconds(_options.SessionTTL),
            Behavior = SessionBehavior.Delete
        };

        var sessionId = (await _consulClient.Session.Create(sessionEntry, cancellationToken)).Response;
        _currentCampaignSessionId = sessionId;
        _sessionRenewalTask = RenewSessionAsync(sessionId, cancellationToken);
        return sessionId;
    }

    /// <summary>
    /// Renews the session periodically to maintain leadership
    /// </summary>
    /// <param name="sessionId">The session ID to renew</param>
    /// <param name="cancellationToken">Cancellation token</param>
    private async Task RenewSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _consulClient.Session.Renew(sessionId, cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(_options.RenewInterval), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (IsSessionInvalid(ex))
            {
                // This session is gone for good; retrying it forever would keep the node
                // permanently ineligible. The election loop owns rebuilding it, so this
                // loop simply stops and lets the replacement loop take over.
                _logger.LogWarning(ex,
                    "Session {SessionId} expired; stopping its renewal loop", sessionId);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error renewing session");
                await DelayQuietlyAsync(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
    }

    /// <summary>
    /// True when Consul is telling us the session no longer exists, in whichever of the
    /// several shapes it uses to say so.
    /// </summary>
    private static bool IsSessionInvalid(Exception ex) =>
        ex is SessionExpiredException ||
        (ex is ConsulRequestException requestException && ConsulLeadershipLease.IsSessionGone(requestException));

    /// <summary>
    /// Delays without turning cancellation into an exception the caller has to catch.
    /// </summary>
    private static async Task DelayQuietlyAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Caller's loop condition handles it.
        }
    }

    /// <summary>
    /// Checks if this instance is currently the leader
    /// </summary>
    /// <returns>True if this instance is the leader, false otherwise</returns>
    [Obsolete(
        "IsLeaderAsync has a check-then-act race: leadership can move to another " +
        "instance between this call and the work it guards, so two instances can " +
        "run the same work concurrently. Use " +
        "ILeadershipLeaseProvider.TryAcquireLeadershipAsync, hold the returned " +
        "ILeadershipLease for the duration of the work, observe its LostToken, and " +
        "pass its FencingToken to the resource you are protecting. See " +
        "https://github.com/FrancoPachue/dleader-consul#what-this-does-not-guarantee")]
    public async Task<bool> IsLeaderAsync()
    {
        ThrowIfDisposed();
        try
        {
            var currentLeader = await GetCurrentLeaderAsync();
            return currentLeader == _instanceId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking leadership status");
            return false;
        }
    }

    /// <summary>
    /// Gets the ID of the instance that is currently the leader
    /// </summary>
    /// <returns>The instance ID of the current leader, or empty string if no leader</returns>
    /// <exception cref="ConsulException">Thrown when unable to get the current leader</exception>
    public async Task<string> GetCurrentLeaderAsync()
    {
        ThrowIfDisposed();
        try
        {
            var pair = await _consulClient.KV.Get(_lockKey, CancellationToken.None);
            if (pair.Response is null)
                return string.Empty;

            // A key with no session attached is a leftover from a leader that failed or
            // released, not a leader. Sessions using Release behaviour leave the old
            // value in place, so reporting the value without this check would name an
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
    /// Gets the host address for the service registration
    /// </summary>
    /// <returns>The host address</returns>
    private string GetHostAddress()
    {
        return Environment.GetEnvironmentVariable("HOSTNAME") ?? "localhost";
    }

    /// <summary>
    /// Raises the leadership acquired event
    /// </summary>
    /// <returns>A task representing the event handling</returns>
    public async Task RaiseLeadershipAcquiredEvent()
    {
        if (OnLeadershipAcquired != null)
        {
            try
            {
                await OnLeadershipAcquired.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in OnLeadershipAcquired event handler");
            }
        }
    }

    /// <summary>
    /// Raises the leadership lost event
    /// </summary>
    /// <returns>A task representing the event handling</returns>
    public async Task RaiseLeadershipLostEvent()
    {
        if (OnLeadershipLost != null)
        {
            try
            {
                await OnLeadershipLost.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in OnLeadershipLost event handler");
            }
        }
    }

    /// <summary>
    /// Throws an ObjectDisposedException if the instance has been disposed
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown if the instance has been disposed</exception>
    protected virtual void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ConsulLeaderElection));
        }
    }

    /// <summary>
    /// Releases the resources used by the instance.
    /// </summary>
    /// <remarks>
    /// Prefer <see cref="DisposeAsync"/>. This overload exists because the DI container
    /// may resolve <see cref="IDisposable"/>, and it has to do the same cleanup rather
    /// than leave the lock held until the session times out. The work is pushed to the
    /// thread pool before being waited on, which is what keeps a direct blocking wait
    /// from deadlocking against an ambient synchronization context, and it is bounded
    /// by a timeout so that an unreachable Consul cannot hang shutdown.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            GC.SuppressFinalize(this);
            return;
        }

        try
        {
            if (!Task.Run(() => DisposeAsync().AsTask()).Wait(SyncDisposeTimeout))
            {
                _logger.LogWarning(
                    "Synchronous disposal of {ServiceId} timed out after {Timeout}",
                    _instanceId, SyncDisposeTimeout);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during synchronous disposal for service: {ServiceId}", _instanceId);
        }

        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        try
        {
            _cts?.Cancel();

            // Wait for background tasks to finish gracefully
            var tasks = new List<Task>();
            if (_electionTask != null) tasks.Add(_electionTask);
            if (_sessionRenewalTask != null) tasks.Add(_sessionRenewalTask);

            if (tasks.Any())
            {
                try
                {
                    await Task.WhenAll(tasks);
                }
                catch (OperationCanceledException) { /* Expected */ }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error waiting for background tasks to complete");
                }
            }

            _logger.LogInformation("Disposing service with ID: {ServiceId}", _instanceId);
            await _consulClient.Agent.ServiceDeregister(_instanceId, CancellationToken.None);
            _logger.LogInformation("Service deregistered from Consul: {ServiceId}", _instanceId);

            if (_isLeader)
            {
                // Release is scoped to this instance's session, so Consul refuses it if
                // the lock has already moved on. An unconditional delete would instead
                // rip the lock away from whoever holds it now - which is exactly what
                // happens when _isLeader is a stale true.
                var released = await _consulClient.KV.Release(new KVPair(_lockKey)
                {
                    Session = _currentCampaignSessionId,
                    Value = Encoding.UTF8.GetBytes(_instanceId)
                }, CancellationToken.None);

                _logger.LogInformation(
                    "Leadership lock release for service {ServiceId} accepted: {Accepted}",
                    _instanceId, released.Response);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during async disposal for service: {ServiceId}", _instanceId);
        }
        finally
        {
            _electionCts?.Dispose();
            _cts?.Dispose();
            // Do not dispose injected client as it might be shared
        }
        _disposed = true;
    }
}
