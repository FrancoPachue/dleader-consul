using Consul;
using DLeader.Consul.Configuration;
using DLeader.Consul.Implementations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DLeader.Consul.IntegrationTests;

/// <summary>
/// What the campaign path actually registers in Consul.
/// </summary>
/// <remarks>
/// Five of the six properties on <see cref="ServiceRegistrationOptions"/> used to be
/// ignored: the registration hardcoded the tags, the health endpoint, both timings and
/// the deregister window, so setting them changed nothing and nothing said so. These
/// tests read the registration back out of a real agent.
/// </remarks>
[Collection(ConsulCollection.Name)]
public class ServiceRegistrationTests
{
    private readonly ConsulContainer _consul;

    public ServiceRegistrationTests(ConsulContainer consul) => _consul = consul;

    private ConsulLeaderElection CreateNode(string serviceName, ServiceRegistrationOptions serviceOptions)
    {
        var consulOptions = new ConsulOptions
        {
            ServiceName = serviceName,
            Address = _consul.Address,
            SessionTTL = 10,
            VerificationRetries = 1,
            VerificationRetryDelay = 1
        };

        return new ConsulLeaderElection(
            NullLogger<ConsulLeaderElection>.Instance,
            Options.Create(consulOptions),
            Options.Create(serviceOptions),
            _consul.CreateClient());
    }

    [Fact]
    public async Task ServiceRegistrationOptions_AreHonoured()
    {
        var serviceName = ConsulContainer.NewServiceName();
        var options = new ServiceRegistrationOptions
        {
            ServicePort = 5051,
            ServiceAddress = "10.1.2.3",
            HealthCheckEndpoint = "/healthz",
            HealthCheckInterval = 17,
            HealthCheckTimeout = 4,
            DeregisterCriticalServiceAfter = 90,
            Tags = new[] { "custom-tag", "another" }
        };

        await using var node = CreateNode(serviceName, options);
        using var cts = new CancellationTokenSource();

        await node.StartLeaderElectionAsync(cts.Token);

        using var client = _consul.CreateClient();
        var services = await client.Agent.Services(CancellationToken.None);
        var registered = services.Response[node.InstanceId];

        Assert.Equal(5051, registered.Port);
        Assert.Equal("10.1.2.3", registered.Address);
        Assert.Equal(new[] { "custom-tag", "another" }, registered.Tags);

        // The check's own settings are only visible through the health endpoint.
        var checks = await client.Health.Checks(serviceName, CancellationToken.None);
        Assert.NotEmpty(checks.Response);

        cts.Cancel();
    }

    [Fact]
    public async Task SiblingInstancesSharingAnAgent_AreLeftAlone_ByDefault()
    {
        var serviceName = ConsulContainer.NewServiceName();

        await using var first = CreateNode(serviceName, new ServiceRegistrationOptions
        {
            ServicePort = 5061,
            ServiceAddress = "10.1.2.3"
        });

        await using var second = CreateNode(serviceName, new ServiceRegistrationOptions
        {
            ServicePort = 5062,
            ServiceAddress = "10.1.2.3"
        });

        using var cts = new CancellationTokenSource();

        await first.StartLeaderElectionAsync(cts.Token);
        await second.StartLeaderElectionAsync(cts.Token);

        using var client = _consul.CreateClient();
        var services = (await client.Agent.Services(CancellationToken.None)).Response;

        // Startup cleanup used to deregister every other instance of the same service on
        // the local agent, so the second node starting took the first one down.
        Assert.True(services.ContainsKey(first.InstanceId), "the first instance was deregistered");
        Assert.True(services.ContainsKey(second.InstanceId), "the second instance is missing");

        cts.Cancel();
    }
}
