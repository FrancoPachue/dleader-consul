using DLeader.Consul.Abstractions;
using System.Collections.Concurrent;
using System.Text.Json;

namespace DLeader.Consul.Example.Services
{
    public class QueueProcessorService : BackgroundService
    {
        private readonly ILeaderElection _leaderElection;
        private readonly IMessageBroker _messageBroker;
        private readonly ILogger<QueueProcessorService> _logger;
        private readonly ConcurrentDictionary<string, HashSet<string>> _processingItems;
        private readonly ConcurrentDictionary<string, DateTime> _activeNodes;
        private ConcurrentQueue<QueueItem> _mockPendingItems;
        private readonly Random _random;
        private readonly string _nodeId;
        private readonly int _maxConcurrentItems = 5;
        private DateTime _lastItemGenerated = DateTime.MinValue;

        public QueueProcessorService(
            ILeaderElection leaderElection,
            IMessageBroker messageBroker,
            ILogger<QueueProcessorService> logger)
        {
            _leaderElection = leaderElection;
            _messageBroker = messageBroker;
            _logger = logger;
            _processingItems = new ConcurrentDictionary<string, HashSet<string>>();
            _activeNodes = new ConcurrentDictionary<string, DateTime>();
            _mockPendingItems = new ConcurrentQueue<QueueItem>();
            _random = new Random();
            _nodeId = Guid.NewGuid().ToString();

            _messageBroker.SubscribeAsync("new-item", HandleNewItem);
            _messageBroker.SubscribeAsync("item-completed", HandleItemCompleted);
            _messageBroker.SubscribeAsync("node-heartbeat", HandleNodeHeartbeat);
            _messageBroker.SubscribeAsync("item-failed", HandleItemFailed);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await _leaderElection.StartLeaderElectionAsync(stoppingToken);
                _logger.LogInformation("Queue processor service started. Node ID: {NodeId}", _nodeId);

                // Iniciar el heartbeat
                _ = SendHeartbeatAsync(stoppingToken);

                while (!stoppingToken.IsCancellationRequested)
                {
                    if (await _leaderElection.IsLeaderAsync())
                    {
                        await RemoveInactiveNodes();
                        await AssignWorkToNodes(stoppingToken);
                    }
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fatal error in queue processor service");
                throw;
            }
        }

        private async Task SendHeartbeatAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var heartbeat = new NodeHeartbeat
                    {
                        NodeId = _nodeId,
                        Timestamp = DateTime.UtcNow,
                        ActiveItemsCount = _processingItems.TryGetValue(_nodeId, out var items) ? items.Count : 0
                    };

                    await _messageBroker.BroadcastAsync("node-heartbeat",
                        JsonSerializer.Serialize(heartbeat));

                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error sending heartbeat");
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
        }

        private async Task HandleNodeHeartbeat(string message)
        {
            try
            {
                var heartbeat = JsonSerializer.Deserialize<NodeHeartbeat>(message);
                if (heartbeat != null)
                {
                    _activeNodes.AddOrUpdate(
                        heartbeat.NodeId,
                        heartbeat.Timestamp,
                        (_, _) => heartbeat.Timestamp);

                    _logger.LogDebug("Heartbeat received from node {NodeId}. Active items: {ItemCount}",
                        heartbeat.NodeId, heartbeat.ActiveItemsCount);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing node heartbeat");
            }
        }

        private async Task RemoveInactiveNodes()
        {
            var threshold = DateTime.UtcNow.AddSeconds(-30);
            var inactiveNodes = _activeNodes
                .Where(node => node.Value < threshold)
                .Select(node => node.Key)
                .ToList();

            foreach (var nodeId in inactiveNodes)
            {
                if (_activeNodes.TryRemove(nodeId, out _))
                {
                    _logger.LogWarning("Node {NodeId} removed due to inactivity", nodeId);

                    if (_processingItems.TryRemove(nodeId, out var items))
                    {
                        foreach (var itemId in items)
                        {
                            await _messageBroker.BroadcastAsync("item-failed",
                                JsonSerializer.Serialize(new { ItemId = itemId, NodeId = nodeId, Error = "Node inactive" }));
                        }
                    }
                }
            }
        }

        private async Task<string> SelectLeastBusyNode()
        {
            var activeNodesList = _activeNodes
                .Where(n => n.Value >= DateTime.UtcNow.AddSeconds(-30))
                .Select(n => n.Key)
                .ToList();

            if (!activeNodesList.Any())
            {
                throw new InvalidOperationException("No active nodes available");
            }

            var leastBusyNode = activeNodesList
                .Select(nodeId => new
                {
                    NodeId = nodeId,
                    ItemCount = _processingItems.TryGetValue(nodeId, out var items) ? items.Count : 0
                })
                .OrderBy(n => n.ItemCount)
                .First();

            return leastBusyNode.NodeId;
        }

        private async Task<IEnumerable<QueueItem>> GetPendingItems()
        {
            if (DateTime.UtcNow - _lastItemGenerated > TimeSpan.FromSeconds(30))
            {
                GenerateMockItems();
                _lastItemGenerated = DateTime.UtcNow;
            }

            return _mockPendingItems.Take(10).ToList();
        }

        private void GenerateMockItems()
        {
            var numberOfItems = _random.Next(1, 6);

            for (int i = 0; i < numberOfItems; i++)
            {
                var item = new QueueItem
                {
                    Id = Guid.NewGuid().ToString(),
                    Data = GenerateRandomData(),
                    Priority = _random.Next(1, 4),
                    CreatedAt = DateTime.UtcNow
                };

                _mockPendingItems.Enqueue(item);
                _logger.LogInformation("Generated mock item: {ItemId} with priority {Priority}",
                    item.Id, item.Priority);
            }
        }

        private string GenerateRandomData()
        {
            var taskTypes = new[] { "email", "notification", "report", "backup", "sync" };
            var taskType = taskTypes[_random.Next(taskTypes.Length)];

            return JsonSerializer.Serialize(new
            {
                TaskType = taskType,
                TargetId = _random.Next(1000, 9999),
                Payload = $"Mock data for {taskType} task"
            });
        }

        private async Task AssignWorkToNodes(CancellationToken stoppingToken)
        {
            try
            {
                var pendingItems = await GetPendingItems();

                foreach (var item in pendingItems.Where(i => !IsItemBeingProcessed(i.Id)))
                {
                    try
                    {
                        var nodeId = await SelectLeastBusyNode();
                        await AssignItemToNode(item, nodeId);
                    }
                    catch (InvalidOperationException ex)
                    {
                        _logger.LogWarning(ex, "No nodes available to process item {ItemId}", item.Id);
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error assigning item {ItemId}", item.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in work assignment loop");
            }
        }

        private bool IsItemBeingProcessed(string itemId)
        {
            return _processingItems.Values.Any(items => items.Contains(itemId));
        }

        private async Task HandleNewItem(string message)
        {
            try
            {
                var item = JsonSerializer.Deserialize<QueueItem>(message);
                _logger.LogInformation("New item received: {ItemId}", item?.Id);

                if (item != null && await _leaderElection.IsLeaderAsync())
                {
                    var nodeId = await SelectLeastBusyNode();
                    await AssignItemToNode(item, nodeId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling new item");
            }
        }

        private async Task HandleItemCompleted(string message)
        {
            try
            {
                var completedItem = JsonSerializer.Deserialize<CompletedItem>(message);

                if (completedItem != null && _processingItems.TryGetValue(completedItem.NodeId, out var items))
                {
                    items.Remove(completedItem.ItemId);
                    _logger.LogInformation("Item completed: {ItemId} by node {NodeId} in {ProcessingTime}s",
                        completedItem.ItemId, completedItem.NodeId, completedItem.ProcessingTime.TotalSeconds);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling completed item");
            }
        }

        private async Task HandleItemFailed(string message)
        {
            try
            {
                var failedItem = JsonSerializer.Deserialize<dynamic>(message);
                _logger.LogWarning($"Item {failedItem.ItemId} failed on node {failedItem.NodeId}: {failedItem.Error}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling failed item");
            }
        }

        private async Task AssignItemToNode(QueueItem item, string nodeId)
        {
            var items = _processingItems.GetOrAdd(nodeId, _ => new HashSet<string>());

            if (items.Count < _maxConcurrentItems)
            {
                items.Add(item.Id);

                var remainingItems = new ConcurrentQueue<QueueItem>(
                    _mockPendingItems.Where(i => i.Id != item.Id));
                _mockPendingItems = remainingItems;

                await _messageBroker.BroadcastAsync("item-assigned",
                    JsonSerializer.Serialize(new
                    {
                        ItemId = item.Id,
                        NodeId = nodeId,
                        item.Priority,
                        item.EstimatedProcessingTime,
                        AssignedAt = DateTime.UtcNow
                    }));

                _logger.LogInformation(
                    "Item {ItemId} (Priority: {Priority}) assigned to node {NodeId}. " +
                    "Estimated processing time: {EstimatedTime}s",
                    item.Id, item.Priority, nodeId, item.EstimatedProcessingTime.TotalSeconds);

                _ = SimulateItemProcessingAsync(item, nodeId);
            }
        }

        private async Task SimulateItemProcessingAsync(QueueItem item, string nodeId)
        {
            try
            {
                await Task.Delay(item.EstimatedProcessingTime);

                if (_random.NextDouble() > 0.1)
                {
                    await _messageBroker.BroadcastAsync("item-completed",
                        JsonSerializer.Serialize(new CompletedItem
                        {
                            ItemId = item.Id,
                            NodeId = nodeId,
                            ProcessingTime = item.EstimatedProcessingTime,
                            Result = "Success"
                        }));
                }
                else
                {
                    throw new Exception("Simulated random failure");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing item {ItemId} on node {NodeId}", item.Id, nodeId);

                await _messageBroker.BroadcastAsync("item-failed",
                    JsonSerializer.Serialize(new
                    {
                        ItemId = item.Id,
                        NodeId = nodeId,
                        Error = ex.Message,
                        FailedAt = DateTime.UtcNow
                    }));
            }
            finally
            {
                if (_processingItems.TryGetValue(nodeId, out var items))
                {
                    items.Remove(item.Id);
                }
            }
        }

        private class QueueItem
        {
            public string Id { get; set; }
            public string Data { get; set; }
            public int Priority { get; set; }
            public DateTime CreatedAt { get; set; }
            public TimeSpan EstimatedProcessingTime => TimeSpan.FromSeconds(_random.Next(5, 15));
            private static readonly Random _random = new();
        }

        private class CompletedItem
        {
            public string ItemId { get; set; }
            public string NodeId { get; set; }
            public TimeSpan ProcessingTime { get; set; }
            public string Result { get; set; }
        }

        private class NodeHeartbeat
        {
            public string NodeId { get; set; }
            public DateTime Timestamp { get; set; }
            public int ActiveItemsCount { get; set; }
        }
    }
}