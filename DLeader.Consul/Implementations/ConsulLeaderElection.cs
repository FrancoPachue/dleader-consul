using Consul;
using Microsoft.Extensions.Logging;
using System.Text;
using DLeader.Consul.Configuration;
using System.Net;
using DLeader.Consul.Abstractions;
using DLeader.Consul.Exceptions;
using Microsoft.Extensions.Options;

namespace DLeader.Consul.Implementations;

/// <summary>
/// Distributed leader election implementation using Consul
/// </summary>
public class ConsulLeaderElection : 
    ILeaderElection,
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
    private bool _isLeader;

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

    /// <summary>
    /// Starts the leader election campaign
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to stop the election process</param>
    /// <exception cref="LeadershipException">Thrown when the election process fails to start</exception>
    public async Task StartLeaderElectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            await DeregisterPreviousServiceAsync(cancellationToken);
            await RegisterServiceAsync(cancellationToken);
            await VerifyServiceRegistrationAsync(cancellationToken);
            
            var sessionId = await CreateSessionAsync(cancellationToken);
            _logger.LogInformation("Created Consul session: {SessionId}", sessionId);

            _ = RunLeaderElectionAsync(sessionId, cancellationToken);
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
        _ = RenewSessionAsync(sessionId, cancellationToken);
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in leader election loop");
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error renewing session");
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
    }

    /// <summary>
    /// Checks if this instance is currently the leader
    /// </summary>
    /// <returns>True if this instance is the leader, false otherwise</returns>
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
            var pair = await _consulClient.KV.Get(_lockKey);
            if (pair.Response == null)
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
    /// Releases the resources used by the instance
    /// </summary>
    public void Dispose()
    {
        DisposeAsync().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        try
        {
            _logger.LogInformation("Disposing service with ID: {ServiceId}", _instanceId);
            await _consulClient.Agent.ServiceDeregister(_instanceId);
            _logger.LogInformation("Service deregistered from Consul: {ServiceId}", _instanceId);

            if (_isLeader)
            {
                await _consulClient.KV.Delete(_lockKey);
                _logger.LogInformation("Leadership lock released for service: {ServiceId}", _instanceId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during async disposal for service: {ServiceId}", _instanceId);
        }
        finally
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _consulClient?.Dispose();
        }
        _disposed = true;
    }
}