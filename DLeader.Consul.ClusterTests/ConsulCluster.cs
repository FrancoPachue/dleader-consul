using Consul;
using DLeader.Consul.Configuration;
using DLeader.Consul.Implementations;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DLeader.Consul.ClusterTests;

/// <summary>
/// Three Consul servers with real Raft consensus.
/// </summary>
/// <remarks>
/// <para>
/// Every other test in this project runs against <c>consul agent -dev</c>: a single
/// node, no quorum, no persistence. That verifies the library's own logic and found
/// several real bugs, but it cannot exercise the thing the README's guarantees actually
/// rest on — that Consul is strongly consistent. A single dev agent has no Raft to lose.
/// </para>
/// <para>
/// This fixture is deliberately not shared with the rest of the suite: these tests stop
/// and start servers, and a shared cluster would make them order-dependent.
/// </para>
/// </remarks>
public sealed class ConsulCluster : IAsyncLifetime
{
    private const string Image = "hashicorp/consul:1.20";
    private const int HttpPort = 8500;
    private const int NodeCount = 3;

    private INetwork _network = null!;
    private readonly List<IContainer> _servers = new();

    /// <summary>HTTP address of each server, indexed the same as <see cref="Nodes"/>.</summary>
    public IReadOnlyList<string> Addresses { get; private set; } = Array.Empty<string>();

    /// <summary>Node names, in the order they were started.</summary>
    public IReadOnlyList<string> Nodes { get; private set; } = Array.Empty<string>();

    public async Task InitializeAsync()
    {
        _network = new NetworkBuilder().Build();
        await _network.CreateAsync();

        var names = Enumerable.Range(0, NodeCount).Select(i => $"consul-{i}").ToArray();

        // Every server retry-joins every other by network alias. Consul tolerates
        // joining itself, so one list works for all three and there is no ordering
        // requirement between them.
        var joins = names.SelectMany(n => new[] { "-retry-join", n }).ToArray();

        foreach (var name in names)
        {
            var container = new ContainerBuilder(Image)
                .WithNetwork(_network)
                .WithNetworkAliases(name)
                .WithPortBinding(HttpPort, assignRandomHostPort: true)
                .WithCommand(
                    new[]
                    {
                        "agent", "-server",
                        $"-bootstrap-expect={NodeCount}",
                        "-client=0.0.0.0",
                        $"-node={name}",
                        "-data-dir=/consul/data"
                    }.Concat(joins).ToArray())
                .WithWaitStrategy(
                    Wait.ForUnixContainer()
                        .UntilHttpRequestIsSucceeded(r => r.ForPort(HttpPort).ForPath("/v1/status/leader")))
                .Build();

            await container.StartAsync();
            _servers.Add(container);
        }

        Nodes = names;
        Addresses = _servers
            .Select(c => $"http://{c.Hostname}:{c.GetMappedPublicPort(HttpPort)}")
            .ToList();

        await WaitForLeaderAsync(TimeSpan.FromMinutes(2));
    }

    public async Task DisposeAsync()
    {
        foreach (var server in _servers)
        {
            try
            {
                await server.DisposeAsync();
            }
            catch
            {
                // Best effort; the network teardown below is what matters.
            }
        }

        if (_network is not null)
        {
            await _network.DeleteAsync();
        }
    }

    /// <summary>
    /// Waits until some server reports a Raft leader. An empty string from
    /// <c>/v1/status/leader</c> means the cluster has no leader right now, which is
    /// exactly the state a quorum-loss test wants to observe.
    /// </summary>
    public async Task WaitForLeaderAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (await GetLeaderAsync() is { Length: > 0 })
            {
                return;
            }

            await Task.Delay(500);
        }

        throw new TimeoutException($"The cluster elected no Raft leader within {timeout}.");
    }

    /// <summary>The Raft leader's address, or empty when the cluster has none.</summary>
    public async Task<string> GetLeaderAsync()
    {
        foreach (var index in Enumerable.Range(0, _servers.Count).Where(IsRunning))
        {
            try
            {
                using var client = CreateClient(index);
                var leader = await client.Status.Leader();

                if (!string.IsNullOrWhiteSpace(leader))
                {
                    return leader;
                }
            }
            catch
            {
                // This server may be the one that is down. Ask the next.
            }
        }

        return string.Empty;
    }

    /// <summary>Index of the server currently acting as Raft leader, or -1.</summary>
    /// <remarks>
    /// <c>Status.Leader</c> returns <c>ip:raft-port</c> on the container network, which
    /// is not a host address and cannot be matched against the mapped ports. The
    /// catalog maps that address back to a node name, and the node names are ours
    /// (<c>consul-0</c>..<c>consul-2</c>), so the name is what identifies the index.
    /// </remarks>
    public async Task<int> GetLeaderIndexAsync()
    {
        var leader = await GetLeaderAsync();
        if (leader.Length == 0)
        {
            return -1;
        }

        var ip = leader.Split(':')[0];

        foreach (var index in Enumerable.Range(0, _servers.Count).Where(IsRunning))
        {
            try
            {
                using var client = CreateClient(index);
                var nodes = await client.Catalog.Nodes();

                var match = nodes.Response?.FirstOrDefault(n => n.Address == ip);
                if (match is null)
                {
                    continue;
                }

                var nodeIndex = Nodes.ToList().IndexOf(match.Name);
                if (nodeIndex >= 0)
                {
                    return nodeIndex;
                }
            }
            catch
            {
                // Skip unreachable servers.
            }
        }

        return -1;
    }

    private bool IsRunning(int index) => _stopped.Contains(index) == false;

    private readonly HashSet<int> _stopped = new();

    /// <summary>
    /// Waits until the Raft leader can be resolved to a server index, and returns it.
    /// </summary>
    /// <remarks>
    /// <see cref="GetLeaderIndexAsync"/> answers from a single point in time and returns
    /// -1 whenever the answer is not available yet — during an election, or while the
    /// catalog has not caught up. Asserting on one reading makes a test that passes on
    /// an idle machine and fails on a busy one. Every caller wants "the leader, once
    /// there is one", so that is what this provides.
    /// </remarks>
    public async Task<int> WaitForLeaderIndexAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            var index = await GetLeaderIndexAsync();
            if (index >= 0)
            {
                return index;
            }

            await Task.Delay(500);
        }

        throw new TimeoutException($"Could not resolve the Raft leader within {timeout}.");
    }

    /// <summary>
    /// Waits until the cluster reports a Raft leader that is not
    /// <paramref name="excluding"/>, and returns its index.
    /// </summary>
    /// <remarks>
    /// Waiting only for a non-empty answer from <c>/v1/status/leader</c> is not enough
    /// after stopping the leader: the survivors keep reporting the dead node's address
    /// until the election completes, so a caller that stops the leader and immediately
    /// asks who leads gets the node it just killed. Naming the node to exclude is what
    /// makes this wait mean "the election finished".
    /// </remarks>
    public async Task<int> WaitForNewLeaderAsync(int excluding, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            var index = await GetLeaderIndexAsync();
            if (index >= 0 && index != excluding)
            {
                return index;
            }

            await Task.Delay(500);
        }

        throw new TimeoutException(
            $"No server other than {Nodes[excluding]} became Raft leader within {timeout}.");
    }

    /// <summary>Stops one server, as a crash would.</summary>
    public async Task StopServerAsync(int index)
    {
        await _servers[index].StopAsync();
        _stopped.Add(index);
    }

    /// <summary>Brings a stopped server back.</summary>
    public async Task StartServerAsync(int index)
    {
        await _servers[index].StartAsync();
        _stopped.Remove(index);
    }

    public IConsulClient CreateClient(int index) =>
        new ConsulClient(cfg => cfg.Address = new Uri(Addresses[index]));

    /// <summary>An instance pointed at one specific server of the cluster.</summary>
    public ConsulLeaderElection CreateNode(
        string serviceName,
        int servicePort,
        int serverIndex = 0,
        int sessionTtlSeconds = 10,
        int lockDelaySeconds = 3,
        int safetyMarginSeconds = 2)
    {
        var consulOptions = new ConsulOptions
        {
            ServiceName = serviceName,
            Address = Addresses[serverIndex],
            SessionTTL = sessionTtlSeconds,
            LockDelaySeconds = lockDelaySeconds,
            LeaseSafetyMarginSeconds = safetyMarginSeconds
        };

        return new ConsulLeaderElection(
            NullLogger<ConsulLeaderElection>.Instance,
            Options.Create(consulOptions),
            CreateClient(serverIndex));
    }

    public static string NewServiceName(
        [System.Runtime.CompilerServices.CallerMemberName] string caller = "") =>
        $"{caller.ToLowerInvariant()}-{Guid.NewGuid():N}";
}
