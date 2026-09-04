using System.Collections.Concurrent;
using DLeader.Consul.Configuration;
using DLeader.Consul.Implementations;
using Microsoft.Extensions.Logging.Abstractions;

namespace DLeader.Consul.IntegrationTests;

/// <summary>
/// The broker against a real Consul.
/// </summary>
/// <remarks>
/// Both bugs covered here are invisible to a mock. Duplicate delivery needs a store
/// that actually retains keys and reports real modify indices; the key collision needs
/// a real Put to overwrite a real earlier key. The mocked suite reported neither.
/// </remarks>
[Collection(ConsulCollection.Name)]
public class MessageBrokerTests
{
    private readonly ConsulContainer _consul;

    public MessageBrokerTests(ConsulContainer consul) => _consul = consul;

    private ConsulMessageBroker CreateBroker(string serviceName) =>
        new(serviceName,
            NullLogger<ConsulMessageBroker>.Instance,
            _consul.CreateClient(),
            new MessageBrokerOptions
            {
                // Keep the sweep out of the way; these tests finish in seconds.
                Retention = TimeSpan.FromMinutes(5),
                CleanupInterval = TimeSpan.FromMinutes(5),
                WatchTimeout = TimeSpan.FromSeconds(10)
            });

    [Fact]
    public async Task Subscribers_ReceiveMessagesPublishedAfterTheySubscribe()
    {
        var serviceName = ConsulContainer.NewServiceName();

        await using var publisher = CreateBroker(serviceName);
        await using var subscriber = CreateBroker(serviceName);

        var received = new ConcurrentQueue<string>();
        await subscriber.SubscribeAsync("greeting", m =>
        {
            received.Enqueue(m);
            return Task.CompletedTask;
        });

        await publisher.BroadcastAsync("greeting", "hello");

        await WaitUntilAsync(() => received.Count >= 1, TimeSpan.FromSeconds(20),
            "the subscriber never received the message");

        Assert.Equal(new[] { "hello" }, received.ToArray());
    }

    /// <summary>
    /// The watch loop used to re-dispatch every key still under the prefix whenever any
    /// of them changed, so publishing N messages delivered O(N^2) callbacks and every
    /// handler saw the whole retention window again on each new message.
    /// </summary>
    [Fact]
    public async Task EachMessageIsDeliveredOnce_NotReplayedOnEverySubsequentMessage()
    {
        const int messageCount = 8;
        var serviceName = ConsulContainer.NewServiceName();

        await using var publisher = CreateBroker(serviceName);
        await using var subscriber = CreateBroker(serviceName);

        var received = new ConcurrentQueue<string>();
        await subscriber.SubscribeAsync("tick", m =>
        {
            received.Enqueue(m);
            return Task.CompletedTask;
        });

        for (var i = 0; i < messageCount; i++)
        {
            await publisher.BroadcastAsync("tick", $"message-{i}");
        }

        await WaitUntilAsync(() => received.Count >= messageCount, TimeSpan.FromSeconds(30),
            $"expected {messageCount} messages, saw {received.Count}");

        // Give any spurious replay a chance to show up before asserting.
        await Task.Delay(TimeSpan.FromSeconds(3));

        var all = received.ToArray();
        Assert.Equal(messageCount, all.Length);
        Assert.Equal(all.Length, all.Distinct().Count());
    }

    /// <summary>
    /// Keys were built from <c>DateTime.UtcNow.Ticks</c> alone. The system timer's
    /// resolution is coarse enough that a tight publish loop produces the same tick
    /// repeatedly, and the second Put silently overwrote the first, losing the message.
    /// </summary>
    [Fact]
    public async Task MessagesPublishedInTheSameTick_AreNotLost()
    {
        const int messageCount = 50;
        var serviceName = ConsulContainer.NewServiceName();

        await using var publisher = CreateBroker(serviceName);
        await using var subscriber = CreateBroker(serviceName);

        var received = new ConcurrentQueue<string>();
        await subscriber.SubscribeAsync("burst", m =>
        {
            received.Enqueue(m);
            return Task.CompletedTask;
        });

        // Published as fast as the client allows, which is what collides.
        await Task.WhenAll(Enumerable.Range(0, messageCount)
            .Select(i => publisher.BroadcastAsync("burst", $"burst-{i}")));

        await WaitUntilAsync(() => received.Count >= messageCount, TimeSpan.FromSeconds(45),
            $"only {received.Count} of {messageCount} messages survived");

        Assert.Equal(messageCount, received.Distinct().Count());
    }

    [Fact]
    public async Task AHandlerThatThrows_DoesNotStopTheOthers()
    {
        var serviceName = ConsulContainer.NewServiceName();

        await using var publisher = CreateBroker(serviceName);
        await using var subscriber = CreateBroker(serviceName);

        var healthy = new ConcurrentQueue<string>();

        await subscriber.SubscribeAsync("mixed", _ => throw new InvalidOperationException("boom"));
        await subscriber.SubscribeAsync("mixed", m =>
        {
            healthy.Enqueue(m);
            return Task.CompletedTask;
        });

        await publisher.BroadcastAsync("mixed", "payload");

        await WaitUntilAsync(() => healthy.Count >= 1, TimeSpan.FromSeconds(20),
            "the surviving handler never ran");
    }

    [Fact]
    public async Task DisposeAsync_StopsEveryLoop_WithoutFaulting()
    {
        var serviceName = ConsulContainer.NewServiceName();
        var broker = CreateBroker(serviceName);

        await broker.SubscribeAsync("a", _ => Task.CompletedTask);
        await broker.SubscribeAsync("b", _ => Task.CompletedTask);

        // Used to dispose the token source while the watch loops were still reading its
        // token, which surfaced as ObjectDisposedException on a background thread.
        await broker.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => broker.SubscribeAsync("c", _ => Task.CompletedTask));

        // Disposing twice is a no-op rather than a fault.
        await broker.DisposeAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string because)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(200);
        }

        Assert.Fail(because);
    }
}
