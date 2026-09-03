using Consul;
using DLeader.Consul.Configuration;
using DLeader.Consul.Implementations;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DLeader.Consul.IntegrationTests;

/// <summary>
/// A real Consul agent in a container. Every assertion in this project runs against
/// this rather than a mock, because the behaviour under test - what Consul does to a
/// session it has decided is dead, and when it will let someone else take the lock -
/// is precisely the behaviour a mock has to guess at.
/// </summary>
public sealed class ConsulContainer : IAsyncLifetime
{
    private const string Image = "hashicorp/consul:1.20";
    private const int HttpPort = 8500;

    private IContainer _container = null!;

    public string Address { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        _container = new ContainerBuilder(Image)
            .WithPortBinding(HttpPort, assignRandomHostPort: true)
            .WithCommand("agent", "-dev", "-client=0.0.0.0")
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    .UntilHttpRequestIsSucceeded(r => r
                        .ForPort(HttpPort)
                        .ForPath("/v1/status/leader")))
            .Build();

        await _container.StartAsync();
        Address = $"http://{_container.Hostname}:{_container.GetMappedPublicPort(HttpPort)}";
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>Stops the agent so that every Consul call from the library fails.</summary>
    public Task StopAgentAsync() => _container.StopAsync();

    public IConsulClient CreateClient() =>
        new ConsulClient(cfg => cfg.Address = new Uri(Address));

    /// <summary>
    /// Builds an instance pointed at this container. <paramref name="servicePort"/> is
    /// what makes two instances distinguishable, since the instance id is derived from
    /// service name, host name and port.
    /// </summary>
    public ConsulLeaderElection CreateNode(
        string serviceName,
        int servicePort,
        int sessionTtlSeconds = 10,
        int lockDelaySeconds = 3,
        int safetyMarginSeconds = 2,
        ILogger<ConsulLeaderElection>? logger = null)
    {
        var consulOptions = new ConsulOptions
        {
            ServiceName = serviceName,
            Address = Address,
            SessionTTL = sessionTtlSeconds,
            LockDelaySeconds = lockDelaySeconds,
            LeaseSafetyMarginSeconds = safetyMarginSeconds
        };

        var serviceOptions = new ServiceRegistrationOptions { ServicePort = servicePort };

        return new ConsulLeaderElection(
            logger ?? NullLogger<ConsulLeaderElection>.Instance,
            Options.Create(consulOptions),
            Options.Create(serviceOptions),
            CreateClient());
    }

    /// <summary>A fresh lock key per test, so tests can share one agent.</summary>
    public static string NewServiceName([System.Runtime.CompilerServices.CallerMemberName] string caller = "") =>
        $"{caller.ToLowerInvariant()}-{Guid.NewGuid():N}";
}

[CollectionDefinition(Name)]
public sealed class ConsulCollection : ICollectionFixture<ConsulContainer>
{
    public const string Name = "consul";
}
