namespace DLeader.Consul.Messaging
{
    /// <summary>
    /// Retention and delivery settings for <see cref="IMessageBroker"/>.
    /// </summary>
    /// <remarks>
    /// These bound how long a message survives and how quickly a subscriber notices one.
    /// None of them turn the broker into a durable queue: see
    /// <see cref="IMessageBroker"/> for what it does and does not promise.
    /// </remarks>
    public class MessageBrokerOptions
    {
        /// <summary>
        /// How long a published message stays in the KV store before the cleanup loop
        /// deletes it. Defaults to five minutes.
        /// </summary>
        /// <remarks>
        /// A subscriber that is down for longer than this never sees the messages
        /// published while it was away, and no amount of raising this makes delivery
        /// reliable - it only widens the window.
        /// </remarks>
        public TimeSpan Retention { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// How often expired messages are swept. Defaults to one minute.
        /// </summary>
        public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>
        /// How long a watch blocks waiting for a change before reissuing the query.
        /// Defaults to one minute.
        /// </summary>
        /// <remarks>
        /// This is a long-poll timeout, not a delivery delay: Consul answers as soon as
        /// something changes. Lowering it only increases idle request volume.
        /// </remarks>
        public TimeSpan WatchTimeout { get; set; } = TimeSpan.FromMinutes(1);
    }
}
