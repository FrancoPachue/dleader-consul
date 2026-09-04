namespace DLeader.Consul.Abstractions;

/// <summary>
/// A held claim on leadership, obtained from
/// <see cref="ILeadershipLeaseProvider.TryAcquireLeadershipAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// A lease replaces the check-then-act pattern of the obsolete
/// <c>ILeaderElection.IsLeaderAsync</c>. Instead of asking whether this instance is
/// the leader and then doing work — a gap during which leadership can move to another
/// node — the caller holds a lease for the duration of the work and observes
/// <see cref="LostToken"/> while doing it.
/// </para>
/// <para>
/// <b>What a lease guarantees.</b> While <see cref="LostToken"/> is not cancelled,
/// this instance held the lock at Consul index <see cref="FencingToken"/>, and no
/// other instance held it at any index at or below that value.
/// </para>
/// <para>
/// <b>What it does not guarantee.</b> <see cref="LostToken"/> is a best-effort local
/// signal. It cannot fire while this process is not running: a garbage-collection
/// pause, a hypervisor stall, or a network partition longer than the session TTL can
/// all leave the token uncancelled while another instance has already been granted
/// leadership. Cancellation is therefore never proof of exclusivity — it is only a
/// prompt to stop.
/// </para>
/// <para>
/// <b>Mutual exclusion is only safe if you use the fencing token.</b> Pass
/// <see cref="FencingToken"/> to every side effect, and have the target resource
/// reject any write carrying a token lower than the highest it has already accepted.
/// Without that check at the resource, no distributed lock — this one included — can
/// prevent two writers from overlapping.
/// </para>
/// <para>
/// Disposing the lease releases the Consul session and the underlying lock, and
/// cancels <see cref="LostToken"/>. Always dispose it, ideally with
/// <c>await using</c>.
/// </para>
/// </remarks>
public interface ILeadershipLease : IAsyncDisposable
{
    /// <summary>
    /// Cancelled when this instance is known to have lost, or to be about to lose,
    /// leadership.
    /// </summary>
    /// <remarks>
    /// <para>Cancellation is triggered by whichever of these happens first:</para>
    /// <list type="bullet">
    /// <item><description>the Consul session is reported expired or invalid;</description></item>
    /// <item><description>no session renewal has succeeded within
    /// <c>SessionTTL - LeaseSafetyMarginSeconds</c>, measured on a monotonic local
    /// clock so that wall-clock skew cannot affect it;</description></item>
    /// <item><description>a watch on the lock key observes that it is no longer held
    /// by this lease's session;</description></item>
    /// <item><description>the lease is disposed, or the token passed to
    /// <see cref="ILeadershipLeaseProvider.TryAcquireLeadershipAsync"/> is cancelled.</description></item>
    /// </list>
    /// <para>
    /// Because all four require this process to be scheduled and running, none of
    /// them can fire during a stop-the-world pause. See the remarks on
    /// <see cref="ILeadershipLease"/>.
    /// </para>
    /// </remarks>
    CancellationToken LostToken { get; }

    /// <summary>
    /// The Consul <c>ModifyIndex</c> of the lock key at the moment this instance
    /// acquired it. Strictly increasing across successive leaders.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Consul derives <c>ModifyIndex</c> from the Raft log index, which advances
    /// monotonically across the cluster, so each new leader observes a strictly
    /// greater value than every leader before it. That property is what makes the
    /// value usable for fencing.
    /// </para>
    /// <para>
    /// It holds for the lifetime of a Consul cluster. It does <b>not</b> survive a
    /// restore from snapshot or a rebuild of the cluster from scratch, either of
    /// which can move indices backwards. If the resource you are fencing outlives
    /// the Consul cluster, that is a case you must handle yourself.
    /// </para>
    /// <para>
    /// Send this value with every side effect performed under the lease, and have the
    /// receiving resource reject anything carrying a value lower than the highest it
    /// has accepted so far.
    /// </para>
    /// </remarks>
    long FencingToken { get; }

    /// <summary>
    /// Why the lease was lost, once <see cref="LostToken"/> has been cancelled.
    /// <see langword="null"/> while the lease is still held.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The distinction worth acting on is between
    /// <see cref="LeadershipLostReason.SessionExpired"/> and
    /// <see cref="LeadershipLostReason.LocalDeadlineExceeded"/>. The first means Consul
    /// decided this instance was gone. The second means this instance could not reach
    /// Consul to be told anything and stood down on its own clock — which is what a
    /// network partition looks like from in here.
    /// </para>
    /// <para>
    /// The default implementation returns <see langword="null"/> so that adding this
    /// member did not break existing implementations of the interface.
    /// </para>
    /// </remarks>
    LeadershipLostReason? LostReason => null;
}
