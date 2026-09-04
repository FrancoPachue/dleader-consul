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

    /// <summary>
    /// When set, the agent starts with ACLs enabled, a default policy of deny, and this
    /// value as the management token. Anything presenting a different token, or none,
    /// is refused.
    /// </summary>
    public string? ManagementToken { get; init; }

    /// <summary>The datacenter a dev-mode agent runs in.</summary>
    public const string Datacenter = "dc1";

    public async Task InitializeAsync()
    {
        var builder = new ContainerBuilder(Image)
            .WithPortBinding(HttpPort, assignRandomHostPort: true)
            .WithCommand("agent", "-dev", "-client=0.0.0.0");

        if (ManagementToken is not null)
        {
            // CONSUL_LOCAL_CONFIG is how the official image takes extra configuration.
            // default_policy=deny is the point: without it an unauthenticated request
            // would still succeed and the test would prove nothing.
            var json =
                "{\"acl\":{\"enabled\":true,\"default_policy\":\"deny\"," +
                "\"tokens\":{\"initial_management\":\"" + ManagementToken + "\"}}}";

            builder = builder.WithEnvironment("CONSUL_LOCAL_CONFIG", json);
        }

        _container = builder
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
        new ConsulClient(cfg =>
        {
            cfg.Address = new Uri(Address);

            if (ManagementToken is not null)
            {
                cfg.Token = ManagementToken;
            }
        });

    /// <summary>
    /// Options pointed at this container, with nothing filled in that a test does not
    /// need. Used by the tests that exercise how the library builds its own client,
    /// which is the path <see cref="CreateClient"/> bypasses.
    /// </summary>
    public ConsulOptions CreateOptions(string serviceName) => new()
    {
        ServiceName = serviceName,
        Address = Address,
        SessionTTL = 10,
        LockDelaySeconds = 1,
        LeaseSafetyMarginSeconds = 2
    };

    /// <summary>
    /// Builds an instance that constructs its own Consul client from
    /// <paramref name="consulOptions"/>, rather than being handed one. This is the path
    /// that applies the ACL token and datacenter, so it is the only way to test them.
    /// </summary>
    public static ConsulLeaderElection CreateNodeWithOwnClient(
        ConsulOptions consulOptions,
        int servicePort) =>
        new(NullLogger<ConsulLeaderElection>.Instance, Options.Create(consulOptions));

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

        return new ConsulLeaderElection(
            logger ?? NullLogger<ConsulLeaderElection>.Instance,
            Options.Create(consulOptions),
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
