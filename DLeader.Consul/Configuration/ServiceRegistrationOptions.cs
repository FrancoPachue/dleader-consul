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
    }

}
