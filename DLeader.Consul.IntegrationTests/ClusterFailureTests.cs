using DLeader.Consul.Abstractions;
using DLeader.Consul.Exceptions;

namespace DLeader.Consul.IntegrationTests;

/// <summary>
/// The library against a real three-server Consul cluster, losing quorum and losing its
/// Raft leader.
/// </summary>
/// <remarks>
/// <para>
/// The README states that every leadership guarantee here is Consul's, inherited, and
/// that the library adds no consensus of its own. That claim was previously untested:
/// the rest of the suite runs against a single dev agent, which has no Raft to lose.
/// These tests exercise the layer the guarantees actually rest on.
/// </para>
/// <para>
/// They are slower than the rest of the suite by design — bootstrapping Raft and waiting
/// out an election takes real time, and neither can be faked.
/// </para>
/// </remarks>
// Each test in this class brings up its own three-server cluster, so letting them run
// in parallel would mean a dozen Consul containers at once and elections competing for
// the same CPU. One collection makes them sequential.
[Collection("cluster")]
[Trait("Category", "Cluster")]
public class ClusterFailureTests : IAsyncLifetime
{
    private readonly ConsulCluster _cluster = new();

    public Task InitializeAsync() => _cluster.InitializeAsync();

    public Task DisposeAsync() => _cluster.DisposeAsync();

    [Fact]
    public async Task WithQuorum_LeadershipBehavesNormally()
    {
        var serviceName = ConsulCluster.NewServiceName();

        await using var nodeA = _cluster.CreateNode(serviceName, servicePort: 5001, serverIndex: 0);
        await using var nodeB = _cluster.CreateNode(serviceName, servicePort: 5002, serverIndex: 1);

        // Two library instances talking to two different Consul servers, which is the
        // arrangement a real deployment has and the single-agent suite cannot represent.
        var attempts = await Task.WhenAll(
            nodeA.TryAcquireLeadershipAsync(),
            nodeB.TryAcquireLeadershipAsync());

        var winners = attempts.Where(l => l is not null).ToArray();
        Assert.Single(winners);

        await winners[0]!.DisposeAsync();
    }

    [Fact]
    public async Task LosingQuorum_StopsGrantingLeases_AndTheHeldLeaseGivesUp()
    {
        var serviceName = ConsulCluster.NewServiceName();

        await using var holder = _cluster.CreateNode(
            serviceName, servicePort: 5001, serverIndex: 0, sessionTtlSeconds: 10, safetyMarginSeconds: 2);

        var lease = await holder.TryAcquireLeadershipAsync();
        Assert.NotNull(lease);

        // Two of three servers down: the survivor cannot form a quorum, so Consul stops
        // accepting writes. This is the failure the README calls "availability is lost;
        // safety is not".
        await _cluster.StopServerAsync(1);
        await _cluster.StopServerAsync(2);

        await WaitUntilAsync(
            async () => (await _cluster.GetLeaderAsync()).Length == 0,
            TimeSpan.FromSeconds(60),
            "the cluster still reported a Raft leader after losing quorum");

        // No new leases while there is no quorum.
        await using var challenger = _cluster.CreateNode(
            serviceName, servicePort: 5002, serverIndex: 0, sessionTtlSeconds: 10, safetyMarginSeconds: 2);

        var refused = false;
        try
        {
            refused = await challenger.TryAcquireLeadershipAsync() is null;
        }
        catch (LeadershipException)
        {
            // Consul refusing the write outright is an equally correct answer.
            refused = true;
        }

        Assert.True(refused, "a lease was granted while the cluster had no quorum");

        // And the existing holder stands down on its own local deadline, because its
        // renewals cannot be committed either. Safety is preserved by nobody leading,
        // not by the old leader carrying on.
        await WaitUntilAsync(
            () => Task.FromResult(lease!.LostToken.IsCancellationRequested),
            TimeSpan.FromSeconds(60),
            "the holder never gave up leadership after quorum was lost");

        Assert.Equal(LeadershipLostReason.LocalDeadlineExceeded, lease!.LostReason);

        await lease.DisposeAsync();
    }

    [Fact]
    public async Task LosingTheRaftLeader_KeepsFencingTokensMonotonic()
    {
        var serviceName = ConsulCluster.NewServiceName();

        // Point the library at a server that is not the Raft leader, so stopping the
        // leader exercises Consul's internal failover rather than just killing our own
        // connection.
        var raftLeader = await _cluster.GetLeaderIndexAsync();
        Assert.InRange(raftLeader, 0, 2);

        var survivor = Enumerable.Range(0, 3).First(i => i != raftLeader);

        await using var before = _cluster.CreateNode(serviceName, servicePort: 5001, serverIndex: survivor);
        var first = await before.TryAcquireLeadershipAsync();
        Assert.NotNull(first);

        var tokenBefore = first!.FencingToken;
        await first.DisposeAsync();

        // Kill the Raft leader. Two of three remain, so a quorum survives and the
        // cluster elects a new one.
        //
        // Waiting for "some leader" is not enough: the survivors keep reporting the dead
        // node's address until the election actually completes. Waiting for a leader
        // that is not the one we killed is the condition that means the failover
        // happened.
        await _cluster.StopServerAsync(raftLeader);
        var newLeader = await _cluster.WaitForNewLeaderAsync(raftLeader, TimeSpan.FromSeconds(90));

        Assert.NotEqual(raftLeader, newLeader);

        await using var after = _cluster.CreateNode(serviceName, servicePort: 5002, serverIndex: survivor);
        var second = await AcquireWithRetriesAsync(after, TimeSpan.FromSeconds(60));
        Assert.NotNull(second);

        // The property everything downstream depends on: a Raft election inside Consul
        // does not move indices backwards, so the fencing token still only grows. If
        // this ever fails, resources fencing on the token would start rejecting the
        // real leader.
        Assert.True(
            second!.FencingToken > tokenBefore,
            $"fencing token went from {tokenBefore} to {second.FencingToken} across a Consul leader election");

        await second.DisposeAsync();
    }

    [Fact]
    public async Task RegainingQuorum_RestoresLeadership()
    {
        var serviceName = ConsulCluster.NewServiceName();

        await _cluster.StopServerAsync(2);
        await _cluster.WaitForLeaderAsync(TimeSpan.FromSeconds(60));

        // Two of three is still a quorum, so this should behave normally.
        await using var node = _cluster.CreateNode(serviceName, servicePort: 5001, serverIndex: 0);
        var lease = await AcquireWithRetriesAsync(node, TimeSpan.FromSeconds(60));

        Assert.NotNull(lease);
        Assert.False(lease!.LostToken.IsCancellationRequested);

        await lease.DisposeAsync();

        await _cluster.StartServerAsync(2);
        await _cluster.WaitForLeaderAsync(TimeSpan.FromSeconds(60));
    }

    private static async Task<ILeadershipLease?> AcquireWithRetriesAsync(
        ILeadershipLeaseProvider node, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var lease = await node.TryAcquireLeadershipAsync();
                if (lease is not null)
                {
                    return lease;
                }
            }
            catch (LeadershipException)
            {
                // The cluster may still be settling after an election.
            }

            await Task.Delay(500);
        }

        return null;
    }

    private static async Task WaitUntilAsync(
        Func<Task<bool>> condition, TimeSpan timeout, string because)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(500);
        }

        Assert.Fail(because);
    }
}
