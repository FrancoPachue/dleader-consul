using Consul;
using DLeader.Consul.Abstractions;
using Microsoft.Extensions.Logging;
using System.Text;

namespace DLeader.Consul.Implementations;
    public class ConsulMessageBroker : IMessageBroker, IDisposable
    {
        private readonly IConsulClient _consulClient;
        private readonly string _serviceName;
        private readonly ILogger<ConsulMessageBroker> _logger;
        private readonly Dictionary<string, List<Func<string, Task>>> _handlers;
        private readonly Dictionary<string, ulong> _messageIndexes;
        private readonly CancellationTokenSource _cts;

        public ConsulMessageBroker(
            string serviceName,
            ILogger<ConsulMessageBroker> logger,
            IConsulClient consultClient)
        {
            _serviceName = serviceName;
            _logger = logger;
            _handlers = new Dictionary<string, List<Func<string, Task>>>();
            _messageIndexes = new Dictionary<string, ulong>();
            _cts = new CancellationTokenSource();
            _consulClient = consultClient;
        }

        public async Task BroadcastAsync(string messageType, string payload)
        {
            var key = $"messages/{_serviceName}/{messageType}/{DateTime.UtcNow.Ticks}";
            var pair = new KVPair(key)
            {
                Value = Encoding.UTF8.GetBytes(payload)
            };

            await _consulClient.KV.Put(pair);
        }

        public Task SubscribeAsync(string messageType, Func<string, Task> handler)
        {
            if (!_handlers.ContainsKey(messageType))
            {
                _handlers[messageType] = new List<Func<string, Task>>();
                _messageIndexes[messageType] = 0;
                StartWatching(messageType);
            }

            _handlers[messageType].Add(handler);
            return Task.CompletedTask;
        }

        private void StartWatching(string messageType)
        {
            var prefix = $"messages/{_serviceName}/{messageType}/";

            Task.Run(async () =>
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        var queryOptions = new QueryOptions
                        {
                            WaitIndex = _messageIndexes[messageType]
                        };

                        var response = await _consulClient.KV.List(prefix, queryOptions);
                        if (response.LastIndex > _messageIndexes[messageType])
                        {
                            _messageIndexes[messageType] = response.LastIndex;

                            if (response.Response != null)
                            {
                                foreach (var pair in response.Response)
                                {
                                    var message = Encoding.UTF8.GetString(pair.Value);
                                    await NotifyHandlersAsync(messageType, message);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error watching messages");
                        await Task.Delay(TimeSpan.FromSeconds(1));
                    }
                }
            }, _cts.Token);
        }

        private async Task NotifyHandlersAsync(string messageType, string message)
        {
            _logger.LogInformation(message);
            if (_handlers.TryGetValue(messageType, out var handlers))
            {
                foreach (var handler in handlers.ToList())
                {
                    try
                    {
                        await handler(message);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error in message handler");
                    }
                }
            }
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _consulClient?.Dispose();
        }
    }
