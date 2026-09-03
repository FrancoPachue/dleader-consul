using Consul;
using DLeader.Consul.Abstractions;
using DLeader.Consul.Configuration;
using DLeader.Consul.Implementations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DLeader.Consul.Extensions
{
    /// <summary>
    /// Registration helpers that wire the Consul leader election and messaging
    /// implementations into a dependency injection container.
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Registers both Leader Election and Messaging using Consul.
        /// </summary>
        public static IServiceCollection AddConsulLeadership(
            this IServiceCollection services,
            Action<ConsulOptions>? configureConsul = null,
            Action<ServiceRegistrationOptions>? configureService = null)
        {
            // 1. Configure Options & Core Client
            AddConsulCore(services, configureConsul, configureService);

            // 2. Register Features
            AddConsulLeaderElection(services);
            AddConsulMessaging(services);

            return services;
        }

        /// <summary>
        /// Registers only the Leader Election mechanism using Consul.
        /// Use this if you want to use a different Message Broker (e.g. Pulsar, RabbitMQ).
        /// </summary>
        public static IServiceCollection AddConsulLeaderElection(
            this IServiceCollection services,
            Action<ConsulOptions>? configureConsul = null,
            Action<ServiceRegistrationOptions>? configureService = null)
        {
            AddConsulCore(services, configureConsul, configureService);
            
            // One shared singleton behind both interfaces. They contend for the same
            // lock key, so separate instances would compete with each other.
            services.TryAddSingleton<ConsulLeaderElection>();
            services.TryAddSingleton<ILeaderElection>(sp => sp.GetRequiredService<ConsulLeaderElection>());
            services.TryAddSingleton<ILeadershipLeaseProvider>(sp => sp.GetRequiredService<ConsulLeaderElection>());

            return services;
        }

        /// <summary>
        /// Registers only the Messaging mechanism using Consul KV.
        /// </summary>
        public static IServiceCollection AddConsulMessaging(
            this IServiceCollection services,
            Action<ConsulOptions>? configureConsul = null)
        {
            // Messaging doesn't strictly need ServiceRegistrationOptions, but needs ConsulOptions
            AddConsulCore(services, configureConsul, null);

            services.TryAddSingleton<IMessageBroker>(sp => 
            {
                var consulClient = sp.GetRequiredService<IConsulClient>();
                var logger = sp.GetRequiredService<ILogger<ConsulMessageBroker>>();
                var options = sp.GetRequiredService<IOptions<ConsulOptions>>().Value;
                
                return new ConsulMessageBroker(
                    options.ServiceName, 
                    logger,
                    consulClient);
            });

            return services;
        }

        private static void AddConsulCore(
            IServiceCollection services, 
            Action<ConsulOptions>? configureConsul, 
            Action<ServiceRegistrationOptions>? configureService)
        {
            // AddOptions has to run whether or not a delegate was supplied: without it
            // IOptions<T> is never registered, and resolving ILeaderElection from a
            // container configured by the no-argument overload fails at runtime.
            services.AddOptions();

            if (configureConsul != null)
            {
                services.Configure(configureConsul);
            }

            if (configureService != null)
            {
                services.Configure(configureService);
            }

            // Register IConsulClient only if not already registered
            services.TryAddSingleton<IConsulClient>(sp =>
            {
                var options = sp.GetRequiredService<IOptions<ConsulOptions>>().Value;
                return new ConsulClient(cfg =>
                {
                    if (!string.IsNullOrEmpty(options.Address))
                    {
                        cfg.Address = new Uri(options.Address);
                    }
                });
            });
        }
    }
}
