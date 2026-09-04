namespace DLeader.Consul.Abstractions;

/// <summary>
/// Why a lease stopped being held.
/// </summary>
/// <remarks>
/// The distinction that matters operationally is between
/// <see cref="SessionExpired"/> and <see cref="LocalDeadlineExceeded"/>. The first
/// means Consul decided this node was gone and said so. The second means this node
/// could not reach Consul to be told anything and stood down on its own clock. They
/// look identical from the outside and point at completely different problems.
/// </remarks>
public enum LeadershipLostReason
{
    /// <summary>Consul reported the session as expired or invalid.</summary>
    SessionExpired,

    /// <summary>
    /// No session renewal succeeded within <c>SessionTTL - LeaseSafetyMarginSeconds</c>,
    /// measured on a monotonic local clock. Consul was unreachable, so this node gave up
    /// on its own rather than waiting to be told.
    /// </summary>
    LocalDeadlineExceeded,

    /// <summary>
    /// A watch on the lock key observed it held by a different session, or gone.
    /// </summary>
    LockKeyTaken,

    /// <summary>The lease was disposed by its owner. The ordinary path.</summary>
    Released,

    /// <summary>
    /// The token passed to
    /// <see cref="ILeadershipLeaseProvider.TryAcquireLeadershipAsync"/> was cancelled,
    /// usually because the host is shutting down.
    /// </summary>
    Cancelled
}
