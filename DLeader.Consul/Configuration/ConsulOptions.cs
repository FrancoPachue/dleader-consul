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

        /// <summary>
        /// Seconds Consul refuses to hand the lock to anyone else after the holding
        /// session is invalidated. Defaults to 15, which is also Consul's own default.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the window in which a failed leader is expected to notice its own
        /// loss before a successor can take over, and it is the main safety/latency
        /// dial in the library. Lowering it speeds up failover and shrinks the margin;
        /// setting it to 0 removes the guard entirely.
        /// </para>
        /// <para>
        /// It applies only to leases taken through
        /// <see cref="Abstractions.ILeadershipLeaseProvider"/>, whose sessions use
        /// <c>SessionBehavior.Release</c>. Consul attaches a lock delay to the key the
        /// session was holding, so a session using <c>Delete</c> behaviour — which
        /// removes the key on invalidation — leaves nothing for the delay to apply to.
        /// </para>
        /// </remarks>
        public int LockDelaySeconds { get; set; } = 15;

        /// <summary>
        /// Seconds subtracted from <see cref="SessionTTL"/> to derive the local
        /// deadline at which a lease declares itself lost. Defaults to 2.
        /// </summary>
        /// <remarks>
        /// A lease cancels its <see cref="Abstractions.ILeadershipLease.LostToken"/>
        /// once <c>SessionTTL - LeaseSafetyMarginSeconds</c> has elapsed since its last
        /// successful session renewal, measured on a monotonic clock. The margin covers
        /// the round trip in which Consul may already have invalidated the session
        /// without this node having heard about it. It must be greater than 0 and less
        /// than <see cref="SessionTTL"/>.
        /// </remarks>
        public int LeaseSafetyMarginSeconds { get; set; } = 2;
    }
}
