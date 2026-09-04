using Consul;
using DLeader.Consul.Configuration;

namespace DLeader.Consul.Implementations;

/// <summary>
/// Builds the Consul client from <see cref="ConsulOptions"/>.
/// </summary>
/// <remarks>
/// The library constructs a client in two places — the dependency injection
/// registration, and the fallback inside <see cref="ConsulLeaderElection"/> when none
/// is supplied. They used to configure it separately, which is how the address was
/// applied in one and not the other. One factory keeps them from drifting again, and
/// means a new setting is wired everywhere or nowhere.
/// </remarks>
internal static class ConsulClientFactory
{
    internal static ConsulClient Create(ConsulOptions options) =>
        new(config => Configure(config, options));

    internal static void Configure(ConsulClientConfiguration config, ConsulOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.Address))
        {
            config.Address = new Uri(options.Address);
        }

        if (!string.IsNullOrWhiteSpace(options.AclToken))
        {
            config.Token = options.AclToken;
        }

        if (!string.IsNullOrWhiteSpace(options.Datacenter))
        {
            config.Datacenter = options.Datacenter;
        }
    }
}
