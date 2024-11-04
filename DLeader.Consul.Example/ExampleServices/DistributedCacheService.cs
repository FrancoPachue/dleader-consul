using DLeader.Consul.Abstractions;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Consul;

namespace DLeader.Consul.Example
{
    public class DistributedCacheService : BackgroundService
    {
        private readonly ILeaderElection _leaderElection;
        private readonly IMessageBroker _messageBroker;
        private readonly ILogger<DistributedCacheService> _logger;
        private readonly IConsulClient _consulClient;
        private readonly ConcurrentDictionary<string, CacheEntry> _cache;
        private readonly string _nodeId;
        private readonly DistributedCacheOptions _options;
        private DateTime _lastCleanup = DateTime.MinValue;
        private DateTime _lastMetricsReport = DateTime.MinValue;
        private long _cacheHits;
        private long _cacheMisses;

        public DistributedCacheService(
            ILeaderElection leaderElection,
            IMessageBroker messageBroker,
            ILogger<DistributedCacheService> logger,
            IConsulClient consulClient,
            IOptions<DistributedCacheOptions> options)
        {
            _leaderElection = leaderElection;
            _messageBroker = messageBroker;
            _logger = logger;
            _consulClient = consulClient;
            _options = options.Value;
            _cache = new ConcurrentDictionary<string, CacheEntry>();
            _nodeId = Guid.NewGuid().ToString();

            InitializeMessageHandlers();

        }

        private void InitializeMessageHandlers()
        {
            _messageBroker.SubscribeAsync("cache-invalidate", HandleCacheInvalidation);
            _messageBroker.SubscribeAsync("cache-update", HandleCacheUpdate);
            _messageBroker.SubscribeAsync("cache-sync-request", HandleCacheSyncRequest);
            _messageBroker.SubscribeAsync("cache-sync-response", HandleCacheSyncResponse);
            _messageBroker.SubscribeAsync("cache-metrics-request", HandleMetricsRequest);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await _leaderElection.StartLeaderElectionAsync(stoppingToken);
                _logger.LogInformation("Distributed cache service started. Node ID: {NodeId}", _nodeId);

                await RequestCacheSync();

                while (!stoppingToken.IsCancellationRequested)
                {
                    if (await _leaderElection.IsLeaderAsync())
                    {
                        if (DateTime.UtcNow - _lastCleanup >= _options.CleanupInterval)
                        {
                            await CheckAndCleanExpiredEntries();
                            _lastCleanup = DateTime.UtcNow;
                        }
                        await ReportMetricsIfNeeded();
                    }
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fatal error in distributed cache service");
                throw;
            }
        }

        public async Task<CacheResult<T>> Get<T>(string key)
        {
            try
            {
                if (_cache.TryGetValue(key, out var entry))
                {
                    if (entry.ExpiresAt > DateTime.UtcNow)
                    {
                        entry.LastAccessed = DateTime.UtcNow;
                        Interlocked.Increment(ref _cacheHits);
                        return new CacheResult<T>
                        {
                            Success = true,
                            Value = (T)entry.Value,
                            FromNode = entry.SourceNode
                        };
                    }
                    else
                    {
                        await InvalidateCache(key, "Expired");
                    }
                }

                Interlocked.Increment(ref _cacheMisses);
                return new CacheResult<T> { Success = false };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting cache entry for key: {Key}", key);
                return new CacheResult<T>
                {
                    Success = false,
                    Error = ex.Message
                };
            }
        }

        public async Task Set<T>(string key, T value, TimeSpan? ttl = null, CacheOptions? options = null)
        {
            try
            {
                var effectiveTtl = ttl ?? _options.DefaultTTL;

                if (_cache.Count >= _options.MaxCacheSize)
                {
                    await EvictOldestEntries();
                }

                var entry = new CacheEntry
                {
                    Value = value,
                    ExpiresAt = DateTime.UtcNow.Add(effectiveTtl),
                    LastAccessed = DateTime.UtcNow,
                    CreatedAt = DateTime.UtcNow,
                    SourceNode = _nodeId,
                    Priority = options?.Priority ?? CachePriority.Normal
                };

                _cache.AddOrUpdate(key, entry, (_, _) => entry);

                await _messageBroker.BroadcastAsync("cache-update",
                    JsonSerializer.Serialize(new CacheUpdateMessage
                    {
                        Key = key,
                        Value = value,
                        ExpiresAt = entry.ExpiresAt,
                        SourceNode = _nodeId,
                        Priority = entry.Priority
                    }));

                _logger.LogDebug("Cache entry set: {Key} (TTL: {TTL}s)", key, effectiveTtl.TotalSeconds);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting cache entry for key: {Key}", key);
                throw;
            }
        }

        public async Task InvalidateCache(string key, string reason)
        {
            if (_cache.TryRemove(key, out _))
            {
                await _messageBroker.BroadcastAsync("cache-invalidate",
                    JsonSerializer.Serialize(new CacheInvalidationMessage
                    {
                        Key = key,
                        Reason = reason,
                        SourceNode = _nodeId
                    }));

                _logger.LogInformation("Cache entry invalidated: {Key}. Reason: {Reason}",
                    key, reason);
            }
        }

        private async Task RequestCacheSync()
        {
            try
            {
                await _messageBroker.BroadcastAsync("cache-sync-request",
                    JsonSerializer.Serialize(new CacheSyncRequest
                    {
                        RequestingNode = _nodeId,
                        Timestamp = DateTime.UtcNow
                    }));

                _logger.LogInformation("Cache sync requested by node {NodeId}", _nodeId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error requesting cache sync");
            }
        }

        private async Task HandleCacheSyncRequest(string message)
        {
            try
            {
                var request = JsonSerializer.Deserialize<CacheSyncRequest>(message);

                if (request?.RequestingNode != _nodeId)
                {
                    var snapshot = _cache.ToDictionary(
                        kvp => kvp.Key,
                        kvp => new CacheEntrySnapshot
                        {
                            Value = kvp.Value.Value,
                            ExpiresAt = kvp.Value.ExpiresAt,
                            Priority = kvp.Value.Priority
                        });

                    await _messageBroker.BroadcastAsync("cache-sync-response",
                        JsonSerializer.Serialize(new CacheSyncResponse
                        {
                            SourceNode = _nodeId,
                            TargetNode = request.RequestingNode,
                            Entries = snapshot,
                            Timestamp = DateTime.UtcNow
                        }));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling cache sync request");
            }
        }

        private async Task HandleCacheSyncResponse(string message)
        {
            try
            {
                var response = JsonSerializer.Deserialize<CacheSyncResponse>(message);

                if (response?.TargetNode == _nodeId)
                {
                    foreach (var entry in response.Entries)
                    {
                        if (!_cache.ContainsKey(entry.Key) && entry.Value.ExpiresAt > DateTime.UtcNow)
                        {
                            _cache[entry.Key] = new CacheEntry
                            {
                                Value = entry.Value.Value,
                                ExpiresAt = entry.Value.ExpiresAt,
                                LastAccessed = DateTime.UtcNow,
                                SourceNode = response.SourceNode,
                                Priority = entry.Value.Priority
                            };
                        }
                    }

                    _logger.LogInformation(
                        "Cache sync completed. Received {EntryCount} entries from node {SourceNode}",
                        response.Entries.Count, response.SourceNode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling cache sync response");
            }
        }

        private async Task HandleCacheInvalidation(string message)
        {
            try
            {
                var invalidation = JsonSerializer.Deserialize<CacheInvalidationMessage>(message);

                if (invalidation?.SourceNode != _nodeId)
                {
                    _cache.TryRemove(invalidation.Key, out _);
                    _logger.LogDebug(
                        "Cache entry invalidated from node {SourceNode}: {Key}. Reason: {Reason}",
                        invalidation.SourceNode, invalidation.Key, invalidation.Reason);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling cache invalidation");
            }
        }

        private async Task HandleCacheUpdate(string message)
        {
            try
            {
                var update = JsonSerializer.Deserialize<CacheUpdateMessage>(message);

                if (update?.SourceNode != _nodeId)
                {
                    var entry = new CacheEntry
                    {
                        Value = update.Value,
                        ExpiresAt = update.ExpiresAt,
                        LastAccessed = DateTime.UtcNow,
                        CreatedAt = DateTime.UtcNow,
                        SourceNode = update.SourceNode,
                        Priority = update.Priority
                    };

                    _cache.AddOrUpdate(update.Key, entry, (_, _) => entry);
                    _logger.LogDebug(
                        "Cache entry updated from node {SourceNode}: {Key}",
                        update.SourceNode, update.Key);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling cache update");
            }
        }

        private async Task CheckAndCleanExpiredEntries()
        {
            var now = DateTime.UtcNow;
            var expiredKeys = _cache
                .Where(kvp => kvp.Value.ExpiresAt < now)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredKeys)
            {
                await InvalidateCache(key, "Expired");
            }
        }

        private async Task EvictOldestEntries(int? countToEvict = null)
        {
            var effectiveCountToEvict = countToEvict ?? (_options.MaxCacheSize / 10);

            var oldestEntries = _cache
                .OrderBy(kvp => kvp.Value.Priority)
                .ThenBy(kvp => kvp.Value.LastAccessed)
                .Take(effectiveCountToEvict)
                .ToList();

            foreach (var entry in oldestEntries)
            {
                await InvalidateCache(entry.Key, "Cache size limit reached");
            }
        }

        private async Task ReportMetricsIfNeeded()
        {
            if (DateTime.UtcNow - _lastMetricsReport < TimeSpan.FromMinutes(1))
                return;

            var metrics = new CacheMetrics
            {
                NodeId = _nodeId,
                TotalItems = _cache.Count,
                MemoryUsage = GetCacheMemoryUsage(),
                CacheHits = _cacheHits,
                CacheMisses = _cacheMisses,
                HitRatio = _cacheHits + _cacheMisses == 0 ? 0 :
                    (double)_cacheHits / (_cacheHits + _cacheMisses),
                Timestamp = DateTime.UtcNow
            };

            await _messageBroker.BroadcastAsync("cache-metrics",
                JsonSerializer.Serialize(metrics));

            _lastMetricsReport = DateTime.UtcNow;
        }

        private async Task HandleMetricsRequest(string message)
        {
            await ReportMetricsIfNeeded();
        }

        private long GetCacheMemoryUsage()
        {
            return _cache.Sum(kvp =>
                kvp.Key.Length * sizeof(char) +
                JsonSerializer.Serialize(kvp.Value.Value).Length * sizeof(char));
        }

        #region Supporting Classes

        private class CacheEntry
        {
            public object Value { get; set; }
            public DateTime ExpiresAt { get; set; }
            public DateTime LastAccessed { get; set; }
            public DateTime CreatedAt { get; set; }
            public string SourceNode { get; set; }
            public CachePriority Priority { get; set; }
        }

        private class CacheEntrySnapshot
        {
            public object Value { get; set; }
            public DateTime ExpiresAt { get; set; }
            public CachePriority Priority { get; set; }
        }

        private class CacheUpdateMessage
        {
            public string Key { get; set; }
            public object Value { get; set; }
            public DateTime ExpiresAt { get; set; }
            public string SourceNode { get; set; }
            public CachePriority Priority { get; set; }
        }

        private class CacheInvalidationMessage
        {
            public string Key { get; set; }
            public string Reason { get; set; }
            public string SourceNode { get; set; }
        }

        private class CacheSyncRequest
        {
            public string RequestingNode { get; set; }
            public DateTime Timestamp { get; set; }
        }

        private class CacheSyncResponse
        {
            public string SourceNode { get; set; }
            public string TargetNode { get; set; }
            public Dictionary<string, CacheEntrySnapshot> Entries { get; set; }
            public DateTime Timestamp { get; set; }
        }

        private class CacheMetrics
        {
            public string NodeId { get; set; }
            public int TotalItems { get; set; }
            public long MemoryUsage { get; set; }
            public long CacheHits { get; set; }
            public long CacheMisses { get; set; }
            public double HitRatio { get; set; }
            public DateTime Timestamp { get; set; }
        }
    }

    public class CacheResult<T>
    {
        public bool Success { get; set; }
        public T? Value { get; set; }
        public string? FromNode { get; set; }
        public string? Error { get; set; }
    }

    public class CacheOptions
    {
        public CachePriority Priority { get; set; } = CachePriority.Normal;
    }

    public enum CachePriority
    {
        Low,
        Normal,
        High,
        Critical
    }

    public class DistributedCacheOptions
    {
        public int MaxCacheSize { get; set; } = 10000;
        public TimeSpan DefaultTTL { get; set; } = TimeSpan.FromMinutes(30);
        public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(5);
    }

    public static class DistributedCacheServiceExtensions
    {
        public static IServiceCollection AddDistributedCache(
            this IServiceCollection services,
            Action<DistributedCacheOptions> configureOptions)
        {
            var options = new DistributedCacheOptions();
            configureOptions(options);

            services.Configure<DistributedCacheOptions>(opt =>
            {
                opt.MaxCacheSize = options.MaxCacheSize;
                opt.DefaultTTL = options.DefaultTTL;
                opt.CleanupInterval = options.CleanupInterval;
            });

            services.AddSingleton<DistributedCacheService>();
            return services;
        }

        public static async Task<T?> GetOrCreateAsync<T>(
            this DistributedCacheService cache,
            string key,
            Func<Task<T>> factory,
            TimeSpan? ttl = null,
            CacheOptions? options = null)
        {
            var result = await cache.Get<T>(key);
            if (result.Success)
            {
                return result.Value;
            }

            var value = await factory();
            if (value != null)
            {
                await cache.Set(key, value, ttl, options);
            }

            return value;
        }

        public static async Task<T?> GetOrCreate<T>(
            this DistributedCacheService cache,
            string key,
            Func<T> factory,
            TimeSpan? ttl = null,
            CacheOptions? options = null)
        {
            return await cache.GetOrCreateAsync(
                key,
                () => Task.FromResult(factory()),
                ttl,
                options);
        }

        public static async Task SetManyAsync<T>(
            this DistributedCacheService cache,
            IDictionary<string, T> items,
            TimeSpan ttl,
            CacheOptions? options = null)
        {
            foreach (var item in items)
            {
                await cache.Set(item.Key, item.Value, ttl, options);
            }
        }

        public static async Task<IDictionary<string, CacheResult<T>>> GetManyAsync<T>(
            this DistributedCacheService cache,
            IEnumerable<string> keys)
        {
            var results = new Dictionary<string, CacheResult<T>>();
            foreach (var key in keys)
            {
                results[key] = await cache.Get<T>(key);
            }
            return results;
        }

        public static async Task InvalidateManyAsync(
            this DistributedCacheService cache,
            IEnumerable<string> keys,
            string reason)
        {
            foreach (var key in keys)
            {
                await cache.InvalidateCache(key, reason);
            }
        }

        public static async Task<T?> GetOrDefaultAsync<T>(
            this DistributedCacheService cache,
            string key,
            T? defaultValue = default)
        {
            var result = await cache.Get<T>(key);
            return result.Success ? result.Value : defaultValue;
        }
    }
    #endregion
}