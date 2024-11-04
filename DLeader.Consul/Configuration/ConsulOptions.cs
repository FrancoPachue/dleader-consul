using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLeader.Consul.Configuration
{
    /// <summary>
    /// Configuration options for Consul integration
    /// </summary>
    public class ConsulOptions
    {
        /// <summary>
        /// Name of the service in Consul
        /// </summary>
        public string ServiceName { get; set; } = string.Empty;

        /// <summary>
        /// Address of the Consul server
        /// </summary>
        public string Address { get; set; } = "http://localhost:8500";

        /// <summary>
        /// Time-to-live for the session in seconds
        /// </summary>
        public int SessionTTL { get; set; } = 10;

        /// <summary>
        /// Interval in seconds to check for leadership changes
        /// </summary>
        public int LeaderCheckInterval { get; set; } = 5;

        /// <summary>
        /// Interval in seconds to renew the session
        /// </summary>
        public int RenewInterval { get; set; } = 5;

        /// <summary>
        /// Number of retries for service verification
        /// </summary>
        public int VerificationRetries { get; set; } = 3;

        /// <summary>
        /// Delay in seconds between verification retries
        /// </summary>
        public int VerificationRetryDelay { get; set; } = 1;
    }
}
