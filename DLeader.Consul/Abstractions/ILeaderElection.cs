namespace DLeader.Consul.Abstractions
{
    /// <summary>
    /// Interface for distributed leader election functionality
    /// </summary>
    public interface ILeaderElection
    {
        /// <summary>
        /// Event triggered when leadership is acquired
        /// </summary>
        event Func<Task>? OnLeadershipAcquired;

        /// <summary>
        /// Event triggered when leadership is lost
        /// </summary>
        event Func<Task>? OnLeadershipLost;

        /// <summary>
        /// Gets the unique identifier for this instance
        /// </summary>
        string InstanceId { get; }

        /// <summary>
        /// Starts the leader election process
        /// </summary>
        /// <param name="cancellationToken">Token to cancel the operation</param>
        Task StartLeaderElectionAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Checks if this instance is currently the leader
        /// </summary>
        /// <returns>True if this instance is the leader, false otherwise</returns>
        /// <remarks>
        /// <para>
        /// The returned value describes the past, not the present. Leadership can move
        /// to another instance between this call returning and the caller acting on it,
        /// and no amount of checking closes that gap — a session can be invalidated
        /// during a garbage-collection pause, a hypervisor stall, or a network
        /// partition, all of which are invisible to the caller.
        /// </para>
        /// <para>
        /// Any work guarded only by this method can therefore run on two instances at
        /// once. Use <see cref="ILeadershipLeaseProvider.TryAcquireLeadershipAsync"/>
        /// instead: it returns an <see cref="ILeadershipLease"/> that stays valid for
        /// the duration of the work, carries a fencing token to pass to the resource
        /// being protected, and signals loss through a cancellation token.
        /// </para>
        /// </remarks>
        [Obsolete(
            "IsLeaderAsync has a check-then-act race: leadership can move to another " +
            "instance between this call and the work it guards, so two instances can " +
            "run the same work concurrently. Use " +
            "ILeadershipLeaseProvider.TryAcquireLeadershipAsync, hold the returned " +
            "ILeadershipLease for the duration of the work, observe its LostToken, and " +
            "pass its FencingToken to the resource you are protecting. See " +
            "https://github.com/FrancoPachue/dleader-consul#what-this-does-not-guarantee")]
        Task<bool> IsLeaderAsync();

        /// <summary>
        /// Gets the ID of the current leader
        /// </summary>
        /// <returns>The instance ID of the current leader, or empty string if no leader</returns>
        /// <remarks>
        /// Reports a leader only while the lock key is actually held by a live Consul
        /// session. After a leader fails, the key retains its last value until a
        /// successor acquires it; this method returns an empty string for that
        /// interval rather than naming an instance that is no longer the leader.
        /// </remarks>
        Task<string> GetCurrentLeaderAsync();
    }
}
