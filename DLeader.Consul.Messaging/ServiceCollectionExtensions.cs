using Consul;
using DLeader.Consul.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DLeader.Consul.Messaging;

/// <summary>
/// Registration helpers for Consul-backed messaging.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IMessageBroker"/> over the Consul KV store.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureConsul">Configures <see cref="ConsulOptions"/>.</param>
    /// <param name="configureMessaging">Configures retention and delivery settings.</param>
    /// <remarks>
    /// Safe to call alongside <c>AddConsulLeaderElection</c>: both use
    /// <c>TryAddSingleton</c> and share one <see cref="IConsulClient"/>.
    /// </remarks>
    public static IServiceCollection AddConsulMessaging(
        this IServiceCollection services,
        Action<ConsulOptions>? configureConsul = null,
        Action<MessageBrokerOptions>? configureMessaging = null)
    {
        services.AddOptions();

        if (configureConsul is not null)
        {
            services.Configure(configureConsul);
        }

        if (configureMessaging is not null)
        {
            services.Configure(configureMessaging);
        }

        services.TryAddSingleton<IConsulClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ConsulOptions>>().Value;

            return new ConsulClient(cfg =>
            {
                if (!string.IsNullOrWhiteSpace(options.Address))
                {
                    cfg.Address = new Uri(options.Address);
                }

                if (!string.IsNullOrWhiteSpace(options.AclToken))
                {
                    cfg.Token = options.AclToken;
                }

                if (!string.IsNullOrWhiteSpace(options.Datacenter))
                {
                    cfg.Datacenter = options.Datacenter;
                }
            });
        });

        services.TryAddSingleton<IMessageBroker>(sp =>
        {
            var consulClient = sp.GetRequiredService<IConsulClient>();
            var logger = sp.GetRequiredService<ILogger<ConsulMessageBroker>>();
            var options = sp.GetRequiredService<IOptions<ConsulOptions>>().Value;
            var brokerOptions = sp.GetService<IOptions<MessageBrokerOptions>>()?.Value;

            return new ConsulMessageBroker(
                options.ServiceName,
                logger,
                consulClient,
                brokerOptions);
        });

        return services;
    }
}
