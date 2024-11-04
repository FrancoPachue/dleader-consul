namespace DLeader.Consul.Abstractions;
    public interface IMessageBroker
    {
        Task BroadcastAsync(string messageType, string payload);
        Task SubscribeAsync(string messageType, Func<string, Task> handler);
    }
