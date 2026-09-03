using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLeader.Consul.Configuration
{
    /// <summary>
    /// Configuration options for service registration in Consul
    /// </summary>
    public class ServiceRegistrationOptions
    {
        /// <summary>
        /// Port number where the service is running and will be registered in Consul
        /// </summary>
        public int ServicePort { get; set; }

        /// <summary>
        /// Health check endpoint path. Defaults to "/health"
        /// </summary>
        public string HealthCheckEndpoint { get; set; } = "/health";

        /// <summary>
        /// Interval for health check in seconds. Defaults to 10 seconds
        /// </summary>
        public int HealthCheckInterval { get; set; } = 10;

        /// <summary>
        /// Timeout for health check in seconds. Defaults to 5 seconds
        /// </summary>
        public int HealthCheckTimeout { get; set; } = 5;

        /// <summary>
        /// Time after which a critical service will be deregistered. Defaults to 1 minute
        /// </summary>
        public int DeregisterCriticalServiceAfter { get; set; } = 60;

        /// <summary>
        /// Tags for the service
        /// </summary>
        public string[] Tags { get; set; } = new[] { "leadership-service" };

        /// <summary>
        /// Address Consul should use to reach this instance. When left empty the
        /// <c>HOSTNAME</c> environment variable is used, falling back to
        /// <see cref="Environment.MachineName"/>.
        /// </summary>
        /// <remarks>
        /// The fallback used to be the literal <c>localhost</c>, which registered a
        /// health check pointing at whatever host the Consul agent happened to run on.
        /// That is right only when the agent is a sidecar on the same host, and wrong
        /// everywhere else. Set this explicitly when the address Consul must dial
        /// differs from the machine name - behind a NAT, or in a container whose
        /// hostname does not resolve from the agent.
        /// </remarks>
        public string ServiceAddress { get; set; } = string.Empty;

        /// <summary>
        /// Whether to deregister other instances of the same service found on the local
        /// Consul agent at startup. Defaults to <see langword="false"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This used to happen unconditionally, and it is destructive: <c>Agent.Services</c>
        /// lists everything registered on the local agent, so with two instances of a
        /// service sharing an agent - different ports on one host, or an agent on the
        /// host network - each one deregistered the other on startup.
        /// </para>
        /// <para>
        /// It is also unnecessary. Registering with an ID that already exists replaces
        /// that registration, and a registration whose instance is gone is removed by
        /// Consul after <see cref="DeregisterCriticalServiceAfter"/>. Leave this off
        /// unless you know you have exactly one instance per agent and want stale
        /// entries cleared sooner.
        /// </para>
        /// </remarks>
        public bool DeregisterSiblingInstancesOnStart { get; set; }
    }

}
