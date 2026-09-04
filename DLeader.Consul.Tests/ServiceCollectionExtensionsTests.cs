using Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Consul;
using DLeader.Consul.Configuration;
using DLeader.Consul.Implementations;
using DLeader.Consul.Abstractions;
using DLeader.Consul.Extensions;
using DLeader.Consul.Messaging;
using Moq;

namespace DLeader.Consul.Tests.Extensions
{
    public class ServiceCollectionExtensionsTests
    {
        private readonly IServiceCollection _services = new ServiceCollection();

        public ServiceCollectionExtensionsTests()
        {
            _services.AddLogging();
        }

        [Fact]
        public void AddConsulLeaderElection_WithNoConfiguration_StillProducesAUsableContainer()
        {
            // Regression: options were only registered when a delegate was supplied, so
            // the no-argument overload built a container that threw on resolution.
            _services.AddConsulLeaderElection();

            using var provider = _services.BuildServiceProvider();

            Assert.NotNull(provider.GetService<ILeadershipLeaseProvider>());
            Assert.NotNull(provider.GetService<IConsulClient>());
        }

        [Fact]
        public void AddConsulLeaderElection_AppliesConfiguration()
        {
            _services.AddConsulLeaderElection(consul =>
            {
                consul.ServiceName = "billing";
                consul.Address = "http://consul.internal:8500";
                consul.SessionTTL = 20;
                consul.LockDelaySeconds = 5;
            });

            using var provider = _services.BuildServiceProvider();
            var options = provider.GetRequiredService<IOptions<ConsulOptions>>().Value;

            Assert.Equal("billing", options.ServiceName);
            Assert.Equal("http://consul.internal:8500", options.Address);
            Assert.Equal(20, options.SessionTTL);
            Assert.Equal(5, options.LockDelaySeconds);
        }

        [Fact]
        public void TheInterfaceAndTheConcreteType_ResolveToOneInstance()
        {
            // Two instances would contend for the same lock key with separate sessions,
            // which is a process competing with itself.
            _services.AddConsulLeaderElection(c => c.ServiceName = "billing");

            using var provider = _services.BuildServiceProvider();

            Assert.Same(
                provider.GetRequiredService<ILeadershipLeaseProvider>(),
                provider.GetRequiredService<ConsulLeaderElection>());
        }

        [Fact]
        public void AnAlreadyRegisteredConsulClient_IsKept()
        {
            // The registration uses TryAddSingleton so a caller can supply a client
            // configured their own way - with a custom handler, or an ACL token this
            // library never sees.
            var supplied = new Mock<IConsulClient>().Object;
            _services.AddSingleton(supplied);

            _services.AddConsulLeaderElection(c => c.ServiceName = "billing");

            using var provider = _services.BuildServiceProvider();

            Assert.Same(supplied, provider.GetRequiredService<IConsulClient>());
        }

        [Fact]
        public void AddConsulMessaging_RegistersTheBroker()
        {
            _services.AddConsulMessaging(c => c.ServiceName = "billing");

            using var provider = _services.BuildServiceProvider();

            Assert.IsType<ConsulMessageBroker>(provider.GetService<IMessageBroker>());
        }

        [Fact]
        public void LeadershipAndMessaging_ShareOneConsulClient()
        {
            // They live in separate packages now, but nothing stops a consumer wanting
            // both, and they must not end up with a Consul client each.
            _services.AddConsulLeaderElection(c => c.ServiceName = "billing");
            _services.AddConsulMessaging();

            using var provider = _services.BuildServiceProvider();

            Assert.NotNull(provider.GetService<ILeadershipLeaseProvider>());
            Assert.NotNull(provider.GetService<IMessageBroker>());
            Assert.Same(
                provider.GetRequiredService<IConsulClient>(),
                provider.GetRequiredService<IConsulClient>());
        }
    }
}
