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
        /// ACL token presented to Consul. Empty means no token, which only works on a
        /// cluster with ACLs disabled.
        /// </summary>
        /// <remarks>
        /// <para>
        /// With ACLs enabled — the posture Consul recommends for production — every KV
        /// and session call is rejected without one.
        /// </para>
        /// <para>
        /// The minimum policy the lease API needs is write on the lock key and on
        /// sessions:
        /// </para>
        /// <code>
        /// key_prefix "service/&lt;name&gt;/leader" { policy = "write" }
        /// session_prefix ""                     { policy = "write" }
        /// </code>
        /// <para>
        /// The campaign API additionally needs <c>service_prefix "&lt;name&gt;"</c> with
        /// write, and the message broker needs
        /// <c>key_prefix "messages/&lt;name&gt;/"</c> with write.
        /// </para>
        /// <para>
        /// This value is a credential. It is never logged, and it is only applied to
        /// clients this library constructs: supplying your own <c>IConsulClient</c>
        /// means configuring the token on it yourself.
        /// </para>
        /// </remarks>
        public string AclToken { get; set; } = string.Empty;

        /// <summary>
        /// Consul datacenter to address. Empty means the agent's own datacenter.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Set this to pin the expected datacenter, so a misconfigured agent fails
        /// loudly instead of quietly electing a leader somewhere else.
        /// </para>
        /// <para>
        /// It does not enable leadership across datacenters, and nothing here can.
        /// Consul does not replicate the KV store between datacenters, sessions are
        /// datacenter-scoped, and a fencing token derived from one datacenter's Raft
        /// index is meaningless in another. Elect a leader per datacenter.
        /// </para>
        /// </remarks>
        public string Datacenter { get; set; } = string.Empty;

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
