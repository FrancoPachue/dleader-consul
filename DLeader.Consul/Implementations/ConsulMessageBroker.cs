using Consul;
using DLeader.Consul.Abstractions;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text;

namespace DLeader.Consul.Implementations;

/// <summary>
/// <see cref="IMessageBroker"/> over the Consul KV store: messages are written as
/// short-lived keys under a per-service prefix and subscribers watch that prefix with
/// blocking queries.
/// </summary>
/// <remarks>
/// Delivery is at least once and retention is a few minutes, so this suits coordination
/// notifications and not durable work. See the interface for the full caveats.
/// </remarks>
public class ConsulMessageBroker : IMessageBroker, IDisposable
{
    private readonly IConsulClient _consulClient;
    private readonly string _serviceName;
    private readonly ILogger<ConsulMessageBroker> _logger;
    
    // Thread-safe collections and locks
    private readonly ConcurrentDictionary<string, List<Func<string, Task>>> _handlers = new();
    private readonly ConcurrentDictionary<string, ulong> _messageIndexes = new();
    private readonly object _subscriptionLock = new();
    private readonly ConcurrentDictionary<string, Task> _watchingTasks = new();
    
    private readonly CancellationTokenSource _cts;
    private Task? _cleanupTask;

    /// <summary>Creates a broker bound to one service name.</summary>
    /// <param name="serviceName">Scopes the KV prefix messages are published under.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="consultClient">Consul client. Not disposed by this type, since it is normally a shared singleton.</param>
    public ConsulMessageBroker(
        string serviceName,
        ILogger<ConsulMessageBroker> logger,
        IConsulClient consultClient)
    {
        _serviceName = serviceName;
        _logger = logger;
        _cts = new CancellationTokenSource();
        _consulClient = consultClient;

        // Start the background cleanup task
        _cleanupTask = Task.Run(StartCleanupLoop, _cts.Token);
    }

    /// <inheritdoc />
    public async Task BroadcastAsync(string messageType, string payload)
    {
        // Use a reverse-ordered timestamp or standard one. 
        // Ideally messages are short-lived.
        var key = $"messages/{_serviceName}/{messageType}/{DateTime.UtcNow.Ticks}";
        var pair = new KVPair(key)
        {
            Value = Encoding.UTF8.GetBytes(payload)
        };

        await _consulClient.KV.Put(pair, CancellationToken.None);
    }

    /// <inheritdoc />
    public Task SubscribeAsync(string messageType, Func<string, Task> handler)
    {
        lock (_subscriptionLock)
        {
            if (!_handlers.ContainsKey(messageType))
            {
                _handlers[messageType] = new List<Func<string, Task>>();
                _messageIndexes[messageType] = 0;
                
                // Only start watching if we aren't already
                if (!_watchingTasks.ContainsKey(messageType))
                {
                    var task = Task.Run(() => StartWatching(messageType), _cts.Token);
                    _watchingTasks.TryAdd(messageType, task);
                }
            }

            _handlers[messageType].Add(handler);
        }
        return Task.CompletedTask;
    }

    private async Task StartWatching(string messageType)
    {
        var prefix = $"messages/{_serviceName}/{messageType}/";

        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                var queryOptions = new QueryOptions
                {
                    WaitIndex = _messageIndexes.TryGetValue(messageType, out var index) ? index : 0,
                    WaitTime = TimeSpan.FromMinutes(1) // Long polling timeout
                };

                var response = await _consulClient.KV.List(prefix, queryOptions, _cts.Token);
                
                if (response.LastIndex > _messageIndexes.GetValueOrDefault(messageType, 0UL))
                {
                    _messageIndexes[messageType] = response.LastIndex;

                    if (response.Response != null)
                    {
                        foreach (var pair in response.Response)
                        {
                            // Avoid processing extremely old messages if we just started
                            // (Optional optimization, kept simple here)
                            
                            var message = Encoding.UTF8.GetString(pair.Value);
                            await NotifyHandlersAsync(messageType, message);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break; // Graceful shutdown
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error watching messages for type {MessageType}", messageType);
                await Task.Delay(TimeSpan.FromSeconds(1), _cts.Token);
            }
        }
    }

    private async Task NotifyHandlersAsync(string messageType, string message)
    {
        _logger.LogInformation("Received message [{MessageType}]: {Message}", messageType, message);
        
        List<Func<string, Task>>? handlersCopy = null;
        
        lock (_subscriptionLock)
        {
            if (_handlers.TryGetValue(messageType, out var handlers))
            {
                // Create a copy to iterate safely outside the lock
                handlersCopy = handlers.ToList();
            }
        }

        if (handlersCopy != null)
        {
            foreach (var handler in handlersCopy)
            {
                try
                {
                    await handler(message);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in message handler for {MessageType}", messageType);
                }
            }
        }
    }

    private async Task StartCleanupLoop()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                // Run cleanup every minute
                await Task.Delay(TimeSpan.FromMinutes(1), _cts.Token);

                var prefix = $"messages/{_serviceName}/";
                var keys = await _consulClient.KV.List(prefix, _cts.Token);

                if (keys.Response != null)
                {
                    var cutoff = DateTime.UtcNow.AddMinutes(-5).Ticks; // Delete messages older than 5 minutes
                    
                    foreach (var pair in keys.Response)
                    {
                        // Key format: messages/{service}/{type}/{ticks}
                        var parts = pair.Key.Split('/');
                        if (parts.Length > 0 && long.TryParse(parts.Last(), out var ticks))
                        {
                            if (ticks < cutoff)
                            {
                                await _consulClient.KV.Delete(pair.Key, _cts.Token);
                                _logger.LogDebug("Deleted old message: {Key}", pair.Key);
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in message cleanup loop");
            }
        }
    }

    /// <summary>
    /// Stops the watch and cleanup loops. The injected Consul client is left alone:
    /// it is registered as a singleton and shared with the leader election.
    /// </summary>
    public void Dispose()
    {
        _cts?.Cancel();
        
        // Wait for cleanup gracefully if possible, but don't block forever
        try 
        {
             _cleanupTask?.Wait(500);
        }
        catch { /* Ignore */ }

        _cts?.Dispose();
        // Note: We generally don't dispose the injected IConsulClient here as it might be shared,
        // unless we own it. Based on DI registration, it's a Singleton.
    }
}
