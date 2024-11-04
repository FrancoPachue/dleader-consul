using Xunit;
using Moq;
using Consul;
using Microsoft.Extensions.Logging;
using System.Text;
using DLeader.Consul.Implementations;
using System.Threading.Tasks;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;
using System.Text.Json;

namespace DLeader.Consul.Tests.Implementations
{
    public class ConsulMessageBrokerTests
    {
        private readonly Mock<ILogger<ConsulMessageBroker>> _loggerMock;
        private readonly Mock<IConsulClient> _consulClientMock;
        private readonly Mock<IKVEndpoint> _kvEndpointMock;
        private readonly string _serviceName = "test-service";

        public ConsulMessageBrokerTests()
        {
            _loggerMock = new Mock<ILogger<ConsulMessageBroker>>();
            _consulClientMock = new Mock<IConsulClient>();
            _kvEndpointMock = new Mock<IKVEndpoint>();
            _consulClientMock.Setup(x => x.KV).Returns(_kvEndpointMock.Object);
        }

        [Fact]
        public async Task BroadcastAsync_ShouldPutMessageInConsul()
        {
            // Arrange
            var messageType = "test-message";
            var payload = "test-payload";
            var broker = CreateMessageBroker();

            _kvEndpointMock
                .Setup(x => x.Put(It.IsAny<KVPair>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new WriteResult<bool>());

            // Act
            await broker.BroadcastAsync(messageType, payload);

            // Assert
            _kvEndpointMock.Verify(
                x => x.Put(
                    It.Is<KVPair>(p =>
                        p.Key.StartsWith($"messages/{_serviceName}/{messageType}/") &&
                        Encoding.UTF8.GetString(p.Value) == payload
                    ),
                    It.IsAny<CancellationToken>()
                ),
                Times.Once
            );
        }

        [Fact]
        public async Task SubscribeAsync_ShouldAddHandlerAndStartWatching()
        {
            // Arrange
            var messageType = "test-message";
            var broker = CreateMessageBroker();
            var handlerCalled = false;
            var tcs = new TaskCompletionSource<bool>();

            _kvEndpointMock
                .Setup(x => x.List(It.IsAny<string>(), It.IsAny<QueryOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string key, QueryOptions options, CancellationToken token) =>
                {
                    return new QueryResult<KVPair[]>
                    {
                        LastIndex = options.WaitIndex + 1, // Incrementar el índice
                        Response = new[]
                        {
                    new KVPair($"messages/{messageType}/test")
                    {
                        ModifyIndex = options.WaitIndex + 1,
                        Value = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Content = "test-message-content" }))
                    }
                        }
                    };
                });

            // Act
            await broker.SubscribeAsync(messageType, async (message) =>
            {
                handlerCalled = true;
                tcs.SetResult(true);
                await Task.CompletedTask;
            });

            // Assert
            var result = await Task.WhenAny(tcs.Task, Task.Delay(1000));
            Assert.Same(tcs.Task, result);
            Assert.True(handlerCalled, "Handler was not called within the expected timeframe");

            // Verify Consul interactions
            _kvEndpointMock.Verify(x => x.List(
                It.Is<string>(s => s.Contains(messageType)),
                It.IsAny<QueryOptions>(),
                It.IsAny<CancellationToken>()
            ), Times.AtLeastOnce());
        }

        [Fact]
        public async Task Subscribe_ShouldHandleMultipleHandlersForSameMessageType()
        {
            // Arrange
            var messageType = "test-message";
            var broker = CreateMessageBroker();
            var handler1Called = false;
            var handler2Called = false;
            var tcs1 = new TaskCompletionSource<bool>();
            var tcs2 = new TaskCompletionSource<bool>();

            _kvEndpointMock
                .Setup(x => x.List(It.IsAny<string>(), It.IsAny<QueryOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string key, QueryOptions options, CancellationToken token) =>
                {
                    return new QueryResult<KVPair[]>
                    {
                        LastIndex = options.WaitIndex + 1,
                        Response = new[]
                        {
                    new KVPair($"messages/{messageType}/test")
                    {
                        ModifyIndex = options.WaitIndex + 1,
                        Value = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Content = "test-message-content" }))
                    }
                        }
                    };
                });

            // Act
            await broker.SubscribeAsync(messageType, async (message) =>
            {
                handler1Called = true;
                tcs1.SetResult(true);
                await Task.CompletedTask;
            });

            await broker.SubscribeAsync(messageType, async (message) =>
            {
                handler2Called = true;
                tcs2.SetResult(true);
                await Task.CompletedTask;
            });

            // Wait for both handlers with timeout
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(5));
            var completedTasks = await Task.WhenAny(
                Task.WhenAll(tcs1.Task, tcs2.Task),
                timeoutTask
            );

            // Assert
            Assert.NotEqual(timeoutTask, completedTasks);
            Assert.True(handler1Called, "First handler was not called");
            Assert.True(handler2Called, "Second handler was not called");

            // Verify Consul interactions
            _kvEndpointMock.Verify(x => x.List(
                It.Is<string>(s => s.Contains(messageType)),
                It.IsAny<QueryOptions>(),
                It.IsAny<CancellationToken>()
            ), Times.AtLeastOnce());
        }

        [Fact]
        public async Task Subscribe_ShouldHandleExceptionInHandler()
        {
            // Arrange
            var messageType = "test-message";
            var broker = CreateMessageBroker();
            var normalHandlerCalled = false;

            _kvEndpointMock
                .Setup(x => x.List(It.IsAny<string>(), It.IsAny<QueryOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResult<KVPair[]>
                {
                    LastIndex = 1,
                    Response = new[]
                    {
                        new KVPair("test")
                        {
                            Value = Encoding.UTF8.GetBytes("test-message-content")
                        }
                    }
                });

            // Act
            await broker.SubscribeAsync(messageType, async (_) =>
            {
                throw new Exception("Test exception");
            });

            await broker.SubscribeAsync(messageType, async (_) =>
            {
                normalHandlerCalled = true;
                await Task.CompletedTask;
            });

            // Wait a bit for the background task to process
            await Task.Delay(100);

            // Assert
            Assert.True(normalHandlerCalled);
            _loggerMock.Verify(
                x => x.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.IsAny<It.IsAnyType>(),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()
                ),
                Times.Once
            );
        }

        [Fact]
        public async Task Subscribe_ShouldHandleConsulError()
        {
            // Arrange
            var messageType = "test-message";
            var broker = CreateMessageBroker();

            _kvEndpointMock
                .Setup(x => x.List(It.IsAny<string>(), It.IsAny<QueryOptions>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("Consul error"));

            // Act
            await broker.SubscribeAsync(messageType, async (_) => await Task.CompletedTask);

            // Wait a bit for the background task to process
            await Task.Delay(100);

            // Assert
            _loggerMock.Verify(
                x => x.Log(
                    Microsoft.Extensions.Logging.LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.IsAny<It.IsAnyType>(),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()
                ),
                Times.AtLeastOnce
            );
        }

        [Fact]
        public void Dispose_ShouldCancelBackgroundTasksAndDisposeResources()
        {
            // Arrange
            var broker = CreateMessageBroker();

            // Act
            broker.Dispose();

            // Assert
            _consulClientMock.Verify(x => x.Dispose(), Times.Once);
        }

        private ConsulMessageBroker CreateMessageBroker()
        {
            return new ConsulMessageBroker(
                _serviceName,
                _loggerMock.Object,
                _consulClientMock.Object
            );
        }
    }
}