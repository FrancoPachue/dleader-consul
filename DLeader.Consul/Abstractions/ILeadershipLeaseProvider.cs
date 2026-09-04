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

    /// <summary>
    /// Waits until this instance wins leadership, and returns the lease.
    /// </summary>
    /// <param name="cancellationToken">
    /// Abandons the attempt. Like <see cref="TryAcquireLeadershipAsync"/>, it is linked
    /// to the resulting lease's <see cref="ILeadershipLease.LostToken"/>.
    /// </param>
    /// <returns>A held lease. This method does not return without one.</returns>
    /// <exception cref="OperationCanceledException">
    /// The token was cancelled before leadership was won.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Equivalent to calling <see cref="TryAcquireLeadershipAsync"/> in a loop, which
    /// is what every caller ended up writing. The Consul implementation waits on a
    /// blocking query against the lock key rather than sleeping, so a follower takes
    /// over as soon as the key is released instead of at the next poll.
    /// </para>
    /// <para>
    /// Use <see cref="TryAcquireLeadershipAsync"/> instead when the instance has
    /// follower work to do rather than idling.
    /// </para>
    /// <para>
    /// The default implementation polls. It exists so that adding this method did not
    /// break existing implementations of the interface, and any implementation that can
    /// do better should override it.
    /// </para>
    /// </remarks>
    async Task<ILeadershipLease> AcquireLeadershipAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var lease = await TryAcquireLeadershipAsync(cancellationToken).ConfigureAwait(false);
            if (lease is not null)
            {
                return lease;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }
}
