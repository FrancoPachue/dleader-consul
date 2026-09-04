using Consul;
using DLeader.Consul.Abstractions;
using DLeader.Consul.Configuration;
using DLeader.Consul.Implementations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace DLeader.Consul.Extensions
{
    /// <summary>
    /// Registration helpers that wire Consul leader election into a dependency injection
    /// container.
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Registers Consul leader election.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configureConsul">Configures <see cref="ConsulOptions"/>.</param>
        /// <returns>The same collection, for chaining.</returns>
        /// <remarks>
        /// Registers <see cref="ILeadershipLeaseProvider"/> and an
        /// <see cref="IConsulClient"/>, both as singletons. Register your own
        /// <see cref="IConsulClient"/> beforehand to keep it — this uses
        /// <c>TryAddSingleton</c> and will not replace it. A client you supply is yours
        /// to configure, including its ACL token.
        /// </remarks>
        public static IServiceCollection AddConsulLeaderElection(
            this IServiceCollection services,
            Action<ConsulOptions>? configureConsul = null)
        {
            AddConsulCore(services, configureConsul);

            // One shared singleton behind the interface and the concrete type, so both
            // resolve to the same instance rather than two competing for the same key.
            services.TryAddSingleton<ConsulLeaderElection>();
            services.TryAddSingleton<ILeadershipLeaseProvider>(
                sp => sp.GetRequiredService<ConsulLeaderElection>());

            return services;
        }

        private static void AddConsulCore(
            IServiceCollection services,
            Action<ConsulOptions>? configureConsul)
        {
            // AddOptions has to run whether or not a delegate was supplied: without it
            // IOptions<T> is never registered, and resolving the lease provider from a
            // container configured by the no-argument overload fails at runtime.
            services.AddOptions();

            if (configureConsul != null)
            {
                services.Configure(configureConsul);
            }

            // Register IConsulClient only if not already registered
            services.TryAddSingleton<IConsulClient>(sp =>
            {
                var options = sp.GetRequiredService<IOptions<ConsulOptions>>().Value;
                return ConsulClientFactory.Create(options);
            });
        }
    }
}
