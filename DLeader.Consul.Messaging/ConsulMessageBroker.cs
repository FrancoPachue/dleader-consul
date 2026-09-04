using Consul;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text;

namespace DLeader.Consul.Messaging;

/// <summary>
/// <see cref="IMessageBroker"/> over the Consul KV store: messages are written as
/// short-lived keys under a per-service prefix and subscribers watch that prefix with
/// blocking queries.
/// </summary>
/// <remarks>
/// Delivery is at least once and retention is a few minutes, so this suits coordination
/// notifications and not durable work. See the interface for the full caveats.
/// </remarks>
public class ConsulMessageBroker : IMessageBroker, IDisposable, IAsyncDisposable
{
    private readonly IConsulClient _consulClient;
    private readonly string _serviceName;
    private readonly ILogger<ConsulMessageBroker> _logger;
    private readonly MessageBrokerOptions _options;

    // Thread-safe collections and locks
    private readonly ConcurrentDictionary<string, List<Func<string, Task>>> _handlers = new();
    private readonly object _subscriptionLock = new();
    private readonly ConcurrentDictionary<string, Task> _watchingTasks = new();

    private readonly CancellationTokenSource _cts;
    private readonly Task _cleanupTask;
    private int _disposed;

    /// <summary>Distinguishes messages this instance publishes within the same tick.</summary>
    private long _sequence;

    private static readonly TimeSpan SyncDisposeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Creates a broker bound to one service name.</summary>
    /// <param name="serviceName">Scopes the KV prefix messages are published under.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="consultClient">Consul client. Not disposed by this type, since it is normally a shared singleton.</param>
    /// <param name="options">Retention and delivery settings. Defaults are used when omitted.</param>
    public ConsulMessageBroker(
        string serviceName,
        ILogger<ConsulMessageBroker> logger,
        IConsulClient consultClient,
        MessageBrokerOptions? options = null)
    {
        _serviceName = serviceName;
        _logger = logger;
        _cts = new CancellationTokenSource();
        _consulClient = consultClient;
        _options = options ?? new MessageBrokerOptions();

        // Start the background cleanup task
        _cleanupTask = Task.Run(StartCleanupLoop, CancellationToken.None);
    }

    /// <inheritdoc />
    public async Task BroadcastAsync(string messageType, string payload)
    {
        // The key has to be unique per message. A bare tick count is not: the system
        // timer's resolution is around 15 ms on Windows, so two broadcasts in the same
        // slice produce the same key and the second Put silently overwrites the first.
        // A per-instance sequence and a short instance-unique suffix make collisions
        // impossible while keeping the keys roughly time-ordered.
        var stamp = DateTime.UtcNow.Ticks;
        var sequence = Interlocked.Increment(ref _sequence);

        var key = $"messages/{_serviceName}/{messageType}/{stamp:D19}-{sequence:D10}-{_instanceSuffix}";

        var pair = new KVPair(key)
        {
            Value = Encoding.UTF8.GetBytes(payload)
        };

        await _consulClient.KV.Put(pair, CancellationToken.None);
    }

    private readonly string _instanceSuffix = Guid.NewGuid().ToString("N")[..8];

    /// <inheritdoc />
    public async Task SubscribeAsync(string messageType, Func<string, Task> handler)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        TaskCompletionSource? watchEstablished = null;

        lock (_subscriptionLock)
        {
            if (!_handlers.ContainsKey(messageType))
            {
                _handlers[messageType] = new List<Func<string, Task>>();

                // Only start watching if we aren't already
                if (!_watchingTasks.ContainsKey(messageType))
                {
                    watchEstablished = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);

                    var task = Task.Run(
                        () => StartWatching(messageType, watchEstablished),
                        CancellationToken.None);

                    _watchingTasks.TryAdd(messageType, task);
                }
            }

            _handlers[messageType].Add(handler);
        }

        // Returning before the watch has read its starting index would drop anything
        // published in between: the watch would begin at an index that already includes
        // those messages and never dispatch them. Waiting here makes "published after
        // SubscribeAsync returns" mean something.
        if (watchEstablished is not null)
        {
            await watchEstablished.Task.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Watches one message type and dispatches what is new.
    /// </summary>
    /// <remarks>
    /// The previous implementation re-dispatched every key still under the prefix on
    /// each change, so one new message replayed every message published in the
    /// retention window, to every handler. Tracking the index each key was last
    /// modified at, and dispatching only keys above the last one seen, makes delivery
    /// at least once rather than at least once per subsequent message.
    /// </remarks>
    private async Task StartWatching(string messageType, TaskCompletionSource watchEstablished)
    {
        var prefix = $"messages/{_serviceName}/{messageType}/";
        var token = _cts.Token;

        // Start from the store's current index so that a new subscriber is not handed
        // the backlog of messages published before it existed. These are coordination
        // notifications; a subscriber that was not there did not miss anything it can
        // still act on.
        var lastIndex = await GetCurrentIndexAsync(prefix, token);
        var dispatchedThrough = lastIndex;

        // From here on, anything published is above our starting index and will be
        // delivered. Release the caller of SubscribeAsync.
        watchEstablished.TrySetResult();

        while (!token.IsCancellationRequested)
        {
            try
            {
                var queryOptions = new QueryOptions
                {
                    WaitIndex = lastIndex,
                    WaitTime = _options.WatchTimeout
                };

                var response = await _consulClient.KV.List(prefix, queryOptions, token);

                // Consul's blocking-query contract: an index that moves backwards means
                // the state was reset, and the client must restart from zero.
                lastIndex = response.LastIndex < lastIndex ? 0 : response.LastIndex;

                if (response.Response is null)
                {
                    continue;
                }

                // Ordering by ModifyIndex delivers messages in the order Consul accepted
                // them, which is the only order every subscriber agrees on.
                var fresh = response.Response
                    .Where(pair => pair.ModifyIndex > dispatchedThrough)
                    .OrderBy(pair => pair.ModifyIndex)
                    .ToList();

                foreach (var pair in fresh)
                {
                    var message = pair.Value is null
                        ? string.Empty
                        : Encoding.UTF8.GetString(pair.Value);

                    await NotifyHandlersAsync(messageType, message);
                    dispatchedThrough = Math.Max(dispatchedThrough, pair.ModifyIndex);
                }
            }
            catch (OperationCanceledException)
            {
                break; // Graceful shutdown
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error watching messages for type {MessageType}", messageType);

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task<ulong> GetCurrentIndexAsync(string prefix, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _consulClient.KV.List(prefix, new QueryOptions(), cancellationToken);
            return response.LastIndex;
        }
        catch (Exception ex)
        {
            // Starting from zero replays the retention window once. That is noisier than
            // intended but not incorrect, and it beats failing to subscribe.
            _logger.LogWarning(
                ex, "Could not read the current index for {Prefix}; starting from zero", prefix);
            return 0;
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

        if (handlersCopy is null)
        {
            return;
        }

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

    private async Task StartCleanupLoop()
    {
        var token = _cts.Token;

        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.CleanupInterval, token);

                var prefix = $"messages/{_serviceName}/";
                var keys = await _consulClient.KV.List(prefix, token);

                if (keys.Response is null)
                {
                    continue;
                }

                var cutoff = DateTime.UtcNow.Subtract(_options.Retention).Ticks;

                foreach (var pair in keys.Response)
                {
                    // Key format: messages/{service}/{type}/{ticks}-{sequence}-{suffix}
                    var stamp = pair.Key.Split('/').Last().Split('-').FirstOrDefault();

                    if (long.TryParse(stamp, out var ticks) && ticks < cutoff)
                    {
                        await _consulClient.KV.Delete(pair.Key, token);
                        _logger.LogDebug("Deleted old message: {Key}", pair.Key);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
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
    /// <remarks>
    /// Prefer <see cref="DisposeAsync"/>. This overload blocks, bounded by a timeout,
    /// because the DI container may resolve <see cref="IDisposable"/>.
    /// </remarks>
    public void Dispose()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            GC.SuppressFinalize(this);
            return;
        }

        try
        {
            // Task.Run escapes any ambient synchronization context, so this cannot
            // deadlock the way a direct blocking wait can.
            if (!Task.Run(() => DisposeAsync().AsTask()).Wait(SyncDisposeTimeout))
            {
                _logger.LogWarning(
                    "Synchronous disposal of the message broker timed out after {Timeout}",
                    SyncDisposeTimeout);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disposing the message broker");
        }

        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cts.Cancel();

        // Every loop has to be awaited before the token source goes away. The previous
        // implementation waited only on the cleanup loop and then disposed the source
        // while the watch loops were still using its token.
        var loops = _watchingTasks.Values.Append(_cleanupTask).ToArray();

        foreach (var loop in loops)
        {
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
                _logger.LogError(ex, "Message broker background loop faulted during disposal");
            }
        }

        _watchingTasks.Clear();
        _cts.Dispose();

        GC.SuppressFinalize(this);
    }
}
