namespace DLeader.Consul.Abstractions;

/// <summary>
/// Fan-out of short-lived notifications between the instances of a service, carried
/// over the Consul KV store.
/// </summary>
/// <remarks>
/// This is a convenience for coordination chatter - "leadership moved", "reload your
/// config" - not a message queue. Messages are written as KV entries, delivered at
/// least once, and deleted a few minutes later, so an instance that is down when one
/// is published will never see it. Anything that must not be lost belongs in a real
/// broker.
/// </remarks>
[Obsolete(
    "IMessageBroker is moving to a separate DLeader.Consul.Messaging package in 2.0. " +
    "It has nothing to do with leader election, and shipping a non-durable KV-backed " +
    "fan-out inside a package that promises leadership guarantees invites it to be " +
    "mistaken for a queue. Nothing changes for now; install the messaging package when " +
    "2.0 ships, or move to a real broker if you need delivery guarantees.")]
public interface IMessageBroker
{
    /// <summary>
    /// Publishes a message to every instance subscribed to
    /// <paramref name="messageType"/>.
    /// </summary>
    /// <param name="messageType">Logical channel the message belongs to.</param>
    /// <param name="payload">Message body.</param>
    Task BroadcastAsync(string messageType, string payload);

    /// <summary>
    /// Registers a handler for messages of <paramref name="messageType"/>.
    /// </summary>
    /// <param name="messageType">Logical channel to subscribe to.</param>
    /// <param name="handler">
    /// Invoked for each message. Exceptions it throws are logged and swallowed so that
    /// one failing handler cannot stop the others.
    /// </param>
    /// <remarks>
    /// The returned task completes once the subscription is established, so every
    /// message broadcast after that point is delivered. Messages published before it
    /// are not: a subscriber only sees what happens from the moment it starts watching.
    /// </remarks>
    Task SubscribeAsync(string messageType, Func<string, Task> handler);
}
