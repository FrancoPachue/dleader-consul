using Consul;
using DLeader.Consul.Abstractions;
using DLeader.Consul.Configuration;
using DLeader.Consul.Implementations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DLeader.Consul.Extensions
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddConsulLeadership(
            this IServiceCollection services,
            Action<ConsulOptions> configureConsul = null,
            Action<ServiceRegistrationOptions> configureService = null)
        {
            var consulOptions = new ConsulOptions();
            var serviceOptions = new ServiceRegistrationOptions();

            configureConsul?.Invoke(consulOptions);
            configureService?.Invoke(serviceOptions);

            services.Configure<ConsulOptions>(opt =>
            {
                opt.Address = consulOptions.Address;
                opt.ServiceName = consulOptions.ServiceName;
                opt.SessionTTL = consulOptions.SessionTTL;
                opt.RenewInterval = consulOptions.RenewInterval;
                opt.LeaderCheckInterval = consulOptions.LeaderCheckInterval;
            });

            services.Configure<ServiceRegistrationOptions>(opt =>
            {
                opt.ServicePort = serviceOptions.ServicePort;
                opt.HealthCheckEndpoint = serviceOptions.HealthCheckEndpoint;
                opt.HealthCheckInterval = serviceOptions.HealthCheckInterval;
                opt.HealthCheckTimeout = serviceOptions.HealthCheckTimeout;
                opt.DeregisterCriticalServiceAfter = serviceOptions.DeregisterCriticalServiceAfter;
                opt.Tags = serviceOptions.Tags;
            });

            services.AddSingleton<IConsulClient>(sp =>
            {
                return new ConsulClient(cfg =>
                {
                    cfg.Address = new Uri(consulOptions.Address);
                });
            });

            services.AddSingleton<ILeaderElection, ConsulLeaderElection>();
            services.AddSingleton<IMessageBroker>(sp => 
            {
                var consulClient = sp.GetRequiredService<IConsulClient>();
                var logger = sp.GetRequiredService<ILogger<ConsulMessageBroker>>();
                
                return new ConsulMessageBroker(
                    consulOptions.ServiceName, 
                    logger,
                    consulClient);
            });

            return services;
        }
    }
}
