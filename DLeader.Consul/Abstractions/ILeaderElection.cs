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
        Task<bool> IsLeaderAsync();

        /// <summary>
        /// Gets the ID of the current leader
        /// </summary>
        /// <returns>The instance ID of the current leader, or empty string if no leader</returns>
        Task<string> GetCurrentLeaderAsync();
    }
}
