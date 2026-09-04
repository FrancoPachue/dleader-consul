using DLeader.Consul.Exceptions;

namespace DLeader.Consul.IntegrationTests;

/// <summary>
/// The ACL token and datacenter settings, against an agent that actually enforces them.
/// </summary>
/// <remarks>
/// This gets its own container because it has to run with ACLs enabled and a default
/// policy of deny. Testing an ACL token against an agent that allows everything proves
/// nothing: the call would succeed with or without the token.
/// </remarks>
public class AclAndDatacenterTests : IAsyncLifetime
{
    private const string ManagementToken = "8f3c1d7e-4b2a-4e19-9f6d-5a0c7b2e1d34";

    private readonly ConsulContainer _consul = new() { ManagementToken = ManagementToken };

    public Task InitializeAsync() => _consul.InitializeAsync();

    public Task DisposeAsync() => _consul.DisposeAsync();

    [Fact]
    public async Task WithoutAnAclToken_AcquisitionFails_OnAnAclEnabledCluster()
    {
        var options = _consul.CreateOptions(ConsulContainer.NewServiceName());
        // AclToken deliberately left empty.

        await using var node = ConsulContainer.CreateNodeWithOwnClient(options, servicePort: 5001);

        // This is the state the library shipped in before the token existed: against a
        // cluster configured the way Consul recommends, nothing worked and there was no
        // way to fix it short of registering your own IConsulClient.
        await Assert.ThrowsAsync<LeadershipException>(
            () => node.TryAcquireLeadershipAsync());
    }

    [Fact]
    public async Task WithTheAclToken_LeadershipWorksNormally()
    {
        var options = _consul.CreateOptions(ConsulContainer.NewServiceName());
        options.AclToken = ManagementToken;

        await using var node = ConsulContainer.CreateNodeWithOwnClient(options, servicePort: 5001);

        await using var lease = await node.TryAcquireLeadershipAsync();

        Assert.NotNull(lease);
        Assert.True(lease!.FencingToken > 0);
        Assert.False(lease.LostToken.IsCancellationRequested);
    }

    [Fact]
    public async Task WithAWrongAclToken_AcquisitionFails()
    {
        var options = _consul.CreateOptions(ConsulContainer.NewServiceName());
        options.AclToken = "00000000-0000-0000-0000-000000000000";

        await using var node = ConsulContainer.CreateNodeWithOwnClient(options, servicePort: 5001);

        await Assert.ThrowsAsync<LeadershipException>(
            () => node.TryAcquireLeadershipAsync());
    }

    [Fact]
    public async Task TheCorrectDatacenter_IsAccepted()
    {
        var options = _consul.CreateOptions(ConsulContainer.NewServiceName());
        options.AclToken = ManagementToken;
        options.Datacenter = ConsulContainer.Datacenter;

        await using var node = ConsulContainer.CreateNodeWithOwnClient(options, servicePort: 5001);

        await using var lease = await node.TryAcquireLeadershipAsync();

        Assert.NotNull(lease);
    }

    [Fact]
    public async Task AWrongDatacenter_FailsLoudly_RatherThanElectingSomewhereElse()
    {
        var options = _consul.CreateOptions(ConsulContainer.NewServiceName());
        options.AclToken = ManagementToken;
        options.Datacenter = "dc-that-does-not-exist";

        await using var node = ConsulContainer.CreateNodeWithOwnClient(options, servicePort: 5001);

        // The whole point of naming the datacenter: a misconfigured agent surfaces as an
        // error instead of quietly electing a leader in the wrong place.
        await Assert.ThrowsAsync<LeadershipException>(
            () => node.TryAcquireLeadershipAsync());
    }
}
