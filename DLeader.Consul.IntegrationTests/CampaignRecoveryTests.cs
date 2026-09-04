using Consul;

namespace DLeader.Consul.IntegrationTests;

/// <summary>
/// Regression coverage for the campaign API, which stays in the package for
/// compatibility.
/// </summary>
/// <remarks>
/// Before 1.11.0 an invalidated session left this path permanently wedged: Consul
/// answers an acquire against a dead session with HTTP 500, the loop's catch-all
/// swallowed it, and the code that lowers the leader flag and raises OnLeadershipLost
/// sat on a branch the exception jumped over. The node went on believing it led while
/// a successor already did, and never rebuilt its session, so it could not lead again
/// either. No mock caught it because no mock reproduced the 500.
/// </remarks>
[Collection(ConsulCollection.Name)]
public class CampaignRecoveryTests
{
    private readonly ConsulContainer _consul;

    public CampaignRecoveryTests(ConsulContainer consul) => _consul = consul;

    [Fact]
    public async Task LosingTheSession_RaisesLeadershipLost_AndTheNodeBecomesEligibleAgain()
    {
        var serviceName = ConsulContainer.NewServiceName();
        await using var node = _consul.CreateNode(serviceName, servicePort: 5001);

        var acquired = new TaskCompletionSource();
        var lost = new TaskCompletionSource();
        var acquisitions = 0;

        node.OnLeadershipAcquired += () =>
        {
            Interlocked.Increment(ref acquisitions);
            acquired.TrySetResult();
            return Task.CompletedTask;
        };
        node.OnLeadershipLost += () =>
        {
            lost.TrySetResult();
            return Task.CompletedTask;
        };

        using var cts = new CancellationTokenSource();
        await node.StartLeaderElectionAsync(cts.Token);

        await WithTimeout(acquired.Task, TimeSpan.FromSeconds(30), "never became leader");

        await DestroySessionOfAsync(node.InstanceId);

        // The fix: the invalid-session branch lowers the flag and raises the event.
        await WithTimeout(lost.Task, TimeSpan.FromSeconds(30), "OnLeadershipLost never fired");

        // And rebuilds the session, so the node can lead again rather than staying
        // wedged for the lifetime of the process.
        await WaitUntilAsync(
            () => Volatile.Read(ref acquisitions) >= 2,
            TimeSpan.FromSeconds(60),
            "the node never regained leadership after its session was destroyed");

        cts.Cancel();
    }

    [Fact]
    public async Task GetCurrentLeaderAsync_ReportsNobody_WhileTheKeyIsHeldByNoSession()
    {
        var serviceName = ConsulContainer.NewServiceName();

        await using var nodeA = _consul.CreateNode(serviceName, servicePort: 5001);
        await using var nodeB = _consul.CreateNode(serviceName, servicePort: 5002);

        var lease = await nodeA.TryAcquireLeadershipAsync();
        Assert.NotNull(lease);
        Assert.Equal(nodeA.InstanceId, await nodeB.GetCurrentLeaderAsync());

        await DestroySessionOfAsync(nodeA.InstanceId);

        // Release-behaviour sessions leave the value on the key. Reading it without
        // checking the session would name node A as leader after node A is gone.
        await WaitUntilAsync(
            async () => string.IsNullOrEmpty(await nodeB.GetCurrentLeaderAsync()),
            TimeSpan.FromSeconds(30),
            "kept reporting a leader after the holding session was destroyed");

        await lease!.DisposeAsync();
    }

    // -----------------------------------------------------------------------------

    private async Task DestroySessionOfAsync(string instanceId)
    {
        using var client = _consul.CreateClient();

        var sessions = await client.Session.List();
        var target = sessions.Response.SingleOrDefault(s => s.Name == instanceId);

        Assert.NotNull(target);
        await client.Session.Destroy(target!.ID);
    }

    private static async Task WithTimeout(Task task, TimeSpan timeout, string because)
    {
        var finished = await Task.WhenAny(task, Task.Delay(timeout));
        Assert.True(ReferenceEquals(finished, task), because);
        await task;
    }

    private static Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string because) =>
        WaitUntilAsync(() => Task.FromResult(condition()), timeout, because);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string because)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(250);
        }

        Assert.Fail(because);
    }
}
