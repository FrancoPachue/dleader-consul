namespace DLeader.Consul.Abstractions;

/// <summary>
/// Acquires <see cref="ILeadershipLease"/> instances — the race-free alternative to
/// <c>ILeaderElection.IsLeaderAsync</c>.
/// </summary>
public interface ILeadershipLeaseProvider
{
    /// <summary>
    /// Attempts to acquire leadership once, without waiting.
    /// </summary>
    /// <param name="cancellationToken">
    /// Cancels the acquisition attempt. It is also linked to the resulting lease's
    /// <see cref="ILeadershipLease.LostToken"/>, so cancelling it — on host shutdown,
    /// for example — signals the lease as lost.
    /// </param>
    /// <returns>
    /// A held lease, or <see langword="null"/> if another instance currently holds
    /// leadership. Callers that want to keep trying should retry on an interval; a
    /// failed attempt is cheap and leaves no state behind in Consul.
    /// </returns>
    /// <remarks>
    /// <para>
    /// After a leader fails, Consul enforces a lock delay before anyone else may
    /// acquire the key (see <c>ConsulOptions.LockDelaySeconds</c>). Attempts made
    /// during that window return <see langword="null"/>. This is deliberate: the
    /// delay is the window in which the previous leader is expected to notice its own
    /// loss, and shortening it trades safety for failover speed.
    /// </para>
    /// <para>
    /// Each call creates its own Consul session, which is destroyed when the returned
    /// lease is disposed or when acquisition fails. Sessions are not pooled or reused
    /// across attempts.
    /// </para>
    /// </remarks>
    Task<ILeadershipLease?> TryAcquireLeadershipAsync(CancellationToken cancellationToken = default);
}
