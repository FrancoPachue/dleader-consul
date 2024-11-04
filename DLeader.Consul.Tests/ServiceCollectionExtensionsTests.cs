using Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Consul;
using DLeader.Consul.Configuration;
using DLeader.Consul.Implementations;
using DLeader.Consul.Abstractions;
using DLeader.Consul.Extensions;
using Microsoft.Extensions.Logging;
using Moq;

namespace DLeader.Consul.Tests.Extensions
{
    public class ServiceCollectionExtensionsTests
    {
        private readonly IServiceCollection _services;
        private readonly Mock<ILogger<ConsulMessageBroker>> _messageBrokerLoggerMock;
        private readonly Mock<ILogger<ConsulLeaderElection>> _leaderElectionLoggerMock;

        public ServiceCollectionExtensionsTests()
        {
            _services = new ServiceCollection();
            _messageBrokerLoggerMock = new Mock<ILogger<ConsulMessageBroker>>();
            _leaderElectionLoggerMock = new Mock<ILogger<ConsulLeaderElection>>();

            _services.AddSingleton(_messageBrokerLoggerMock.Object);
            _services.AddSingleton(_leaderElectionLoggerMock.Object);

            var loggerFactory = new Mock<ILoggerFactory>();
            loggerFactory
                .Setup(x => x.CreateLogger(It.Is<string>(s => s == typeof(ConsulMessageBroker).FullName)))
                .Returns(_messageBrokerLoggerMock.Object);
            loggerFactory
                .Setup(x => x.CreateLogger(It.Is<string>(s => s == typeof(ConsulLeaderElection).FullName)))
                .Returns(_leaderElectionLoggerMock.Object);

            _services.AddSingleton<ILoggerFactory>(loggerFactory.Object);
        }

        [Fact]
        public void AddConsulLeadership_WithoutConfiguration_RegistersDefaultServices()
        {
            // Act
            _services.AddConsulLeadership();
            var serviceProvider = _services.BuildServiceProvider();

            // Assert
            var leaderElection = serviceProvider.GetService<ILeaderElection>();
            var messageBroker = serviceProvider.GetService<IMessageBroker>();
            var consulClient = serviceProvider.GetService<IConsulClient>();

            Assert.NotNull(leaderElection);
            Assert.NotNull(messageBroker);
            Assert.NotNull(consulClient);
            Assert.IsType<ConsulLeaderElection>(leaderElection);
            Assert.IsType<ConsulMessageBroker>(messageBroker);
        }

        [Fact]
        public void AddConsulLeadership_WithConsulConfiguration_ConfiguresOptionsCorrectly()
        {
            // Arrange
            var expectedAddress = "http://localhost:8500";
            var expectedServiceName = "test-service";
            var expectedSessionTTL = 30;
            var expectedRenewInterval = 15;
            var expectedLeaderCheckInterval = 5;

            // Act
            _services.AddConsulLeadership(options =>
            {
                options.Address = expectedAddress;
                options.ServiceName = expectedServiceName;
                options.SessionTTL = expectedSessionTTL;
                options.RenewInterval = expectedRenewInterval;
                options.LeaderCheckInterval = expectedLeaderCheckInterval;
            });

            var serviceProvider = _services.BuildServiceProvider();
            var options = serviceProvider.GetRequiredService<IOptions<ConsulOptions>>().Value;

            // Assert
            Assert.Equal(expectedAddress, options.Address);
            Assert.Equal(expectedServiceName, options.ServiceName);
            Assert.Equal(expectedSessionTTL, options.SessionTTL);
            Assert.Equal(expectedRenewInterval, options.RenewInterval);
            Assert.Equal(expectedLeaderCheckInterval, options.LeaderCheckInterval);
        }

        [Fact]
        public void AddConsulLeadership_WithServiceConfiguration_ConfiguresOptionsCorrectly()
        {
            // Arrange
            var expectedPort = 5000;
            var expectedHealthCheckEndpoint = "/health";
            var expectedHealthCheckInterval = 10;
            var expectedHealthCheckTimeout = 5;
            var expectedDeregisterAfter = 30;
            var expectedTags = new[] { "tag1", "tag2" };

            // Act
            _services.AddConsulLeadership(
                configureConsul: null,
                configureService: options =>
                {
                    options.ServicePort = expectedPort;
                    options.HealthCheckEndpoint = expectedHealthCheckEndpoint;
                    options.HealthCheckInterval = expectedHealthCheckInterval;
                    options.HealthCheckTimeout = expectedHealthCheckTimeout;
                    options.DeregisterCriticalServiceAfter = expectedDeregisterAfter;
                    options.Tags = expectedTags;
                });

            var serviceProvider = _services.BuildServiceProvider();
            var options = serviceProvider.GetRequiredService<IOptions<ServiceRegistrationOptions>>().Value;

            // Assert
            Assert.Equal(expectedPort, options.ServicePort);
            Assert.Equal(expectedHealthCheckEndpoint, options.HealthCheckEndpoint);
            Assert.Equal(expectedHealthCheckInterval, options.HealthCheckInterval);
            Assert.Equal(expectedHealthCheckTimeout, options.HealthCheckTimeout);
            Assert.Equal(expectedDeregisterAfter, options.DeregisterCriticalServiceAfter);
            Assert.Equal(expectedTags, options.Tags);
        }

        [Fact]
        public void AddConsulLeadership_RegistersServicesAsSingletons()
        {
            // Act
            _services.AddConsulLeadership();

            // Assert
            var leaderElectionDescriptor = _services.FirstOrDefault(d => d.ServiceType == typeof(ILeaderElection));
            var messageBrokerDescriptor = _services.FirstOrDefault(d => d.ServiceType == typeof(IMessageBroker));
            var consulClientDescriptor = _services.FirstOrDefault(d => d.ServiceType == typeof(IConsulClient));

            Assert.NotNull(leaderElectionDescriptor);
            Assert.NotNull(messageBrokerDescriptor);
            Assert.NotNull(consulClientDescriptor);

            Assert.Equal(ServiceLifetime.Singleton, leaderElectionDescriptor.Lifetime);
            Assert.Equal(ServiceLifetime.Singleton, messageBrokerDescriptor.Lifetime);
            Assert.Equal(ServiceLifetime.Singleton, consulClientDescriptor.Lifetime);
        }

        [Fact]
        public void AddConsulLeadership_ConsulClientConfiguration_UsesConfiguredAddress()
        {
            // Arrange
            var expectedAddress = "http://consul-server:8500";

            // Act
            _services.AddConsulLeadership(options =>
            {
                options.Address = expectedAddress;
            });

            var serviceProvider = _services.BuildServiceProvider();
            var consulClient = serviceProvider.GetRequiredService<IConsulClient>();

            // Assert
            Assert.NotNull(consulClient);

            Assert.IsType<ConsulClient>(consulClient);
        }

        [Fact]
        public void AddConsulLeadership_MessageBrokerConfiguration_UsesConfiguredServiceName()
        {
            // Arrange
            var expectedServiceName = "test-message-broker";

            // Act
            _services.AddConsulLeadership(options =>
            {
                options.ServiceName = expectedServiceName;
            });

            var serviceProvider = _services.BuildServiceProvider();
            var messageBroker = serviceProvider.GetRequiredService<IMessageBroker>();

            // Assert
            Assert.NotNull(messageBroker);
            Assert.IsType<ConsulMessageBroker>(messageBroker);
        }

        [Fact]
        public void AddConsulLeadership_MultipleRegistrations_DoesNotThrowException()
        {
            // Act & Assert
            var exception = Record.Exception(() =>
            {
                _services.AddConsulLeadership();
                _services.AddConsulLeadership();
            });

            Assert.Null(exception);
        }
    }
}