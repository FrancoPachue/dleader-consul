# DLeader.Consul

[![Build](https://github.com/FrancoPachue/dleader-consul/actions/workflows/ci.yml/badge.svg)](https://github.com/FrancoPachue/dleader-consul/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/DLeader.Consul.svg)](https://www.nuget.org/packages/DLeader.Consul)
[![NuGet downloads](https://img.shields.io/nuget/dt/DLeader.Consul.svg)](https://www.nuget.org/packages/DLeader.Consul)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

Distributed leader election for .NET on HashiCorp Consul, built on Consul sessions and
a KV lock.

The library hands you a **lease**: a claim on leadership that stays valid for the
duration of your work, tells you through a `CancellationToken` when it is gone, and
carries a **fencing token** you pass to whatever resource you are protecting. That last
part is the one that actually makes concurrent access safe, and the section on what this
does *not* guarantee explains why.

Targets `net8.0`, `net9.0` and `net10.0`.

```bash
dotnet add package DLeader.Consul
```

Message fan-out between instances lives in a separate package,
[`DLeader.Consul.Messaging`](https://www.nuget.org/packages/DLeader.Consul.Messaging).
It has nothing to do with leader election, and it is not a queue.

---

## Usage

```csharp
builder.Services.AddConsulLeaderElection(consul =>
{
    consul.ServiceName = "invoice-worker";
    consul.Address     = "http://consul:8500";
    consul.SessionTTL  = 10;   // seconds; Consul's minimum
});
```

```csharp
public sealed class InvoiceWorker : BackgroundService
{
    private readonly ILeadershipLeaseProvider _leases;
    private readonly IInvoiceStore _store;

    public InvoiceWorker(ILeadershipLeaseProvider leases, IInvoiceStore store)
        => (_leases, _store) = (leases, store);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Waits on a blocking query rather than polling, so this takes over the
            // moment the lock is free. Use TryAcquireLeadershipAsync instead when the
            // instance has follower work to do rather than idling.
            await using var lease = await _leases.AcquireLeadershipAsync(stoppingToken);

            // Work stops when leadership is lost as well as when the host shuts down.
            using var work = CancellationTokenSource.CreateLinkedTokenSource(
                lease.LostToken, stoppingToken);

            try
            {
                while (!work.IsCancellationRequested)
                {
                    // The fencing token travels with the write. This is the part that
                    // makes the exclusion safe - see "What this does not guarantee".
                    await _store.CloseBatchAsync(lease.FencingToken, work.Token);
                    await Task.Delay(TimeSpan.FromSeconds(5), work.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Leadership moved on, or the host is stopping.
            }
        }
    }
}
```

And the half that most people skip — the resource has to enforce it:

```sql
-- The write is rejected unless it carries a token at least as high as the
-- highest one this row has already accepted.
UPDATE invoice_batches
   SET status = 'closed', fencing_token = @token
 WHERE id = @id
   AND fencing_token <= @token;
```

```csharp
public async Task CloseBatchAsync(long fencingToken, CancellationToken ct)
{
    var rows = await _db.ExecuteAsync(Sql, new { id = _batchId, token = fencingToken }, ct);

    if (rows == 0)
    {
        // A later leader has already written here. This instance is stale, whatever
        // its lease still believes.
        throw new FencedOutException(fencingToken);
    }
}
```

---

## Guarantees

**The model.** Consul is a strongly consistent (CP) store. Leadership is a KV key held
by a session; a session survives only while this process keeps renewing it. Every
guarantee below is Consul's, inherited — this library adds no consensus of its own.

While `lease.LostToken` has not been cancelled:

1. **At most one lease per key.** Consul granted the lock to this session and to no
   other. Two instances cannot both hold a live lease for the same `ServiceName` at the
   same time.
2. **The fencing token is unique and strictly increasing.** `FencingToken` is the
   Consul `ModifyIndex` of the lock key at the moment of acquisition, derived from the
   Raft log index. Every subsequent leader observes a strictly greater value, and no
   value is ever reused.
3. **Leadership loss is signalled, not polled.** `LostToken` is cancelled when the
   session expires, when the lock key is observed in another session's hands, when the
   local deadline passes without a successful renewal, or when the lease is disposed.

### The assumptions these rest on

| Assumption | If it does not hold |
|---|---|
| The Consul cluster keeps a quorum. | No leases are granted, and the holder stands down on its local deadline because its renewals cannot be committed either. Availability is lost; safety is not. Covered by a test against a three-server cluster with two servers stopped. |
| The Consul cluster is not restored from a snapshot or rebuilt. | Raft indices can move backwards, so a new leader's fencing token may be *lower* than an old one's. Guarantee 2 breaks. This is the one failure mode where fencing itself stops protecting you. |
| Only this library writes to `service/{ServiceName}/leader`. | Anything else writing that key can move the lock or the index arbitrarily. |
| The process is scheduled often enough to run its own watchdog. | Cancellation of `LostToken` is delayed by however long the process was frozen. See below. |

**Clock skew does not affect this.** The local deadline is measured with
`Environment.TickCount64`, a monotonic counter. NTP steps, virtual-machine clock drift
and time-zone changes cannot move it. Consul's own TTL accounting is likewise not
wall-clock dependent across nodes.

**Garbage-collection pauses and hypervisor stalls do.** A pause longer than
`SessionTTL` means Consul invalidates the session while the process is frozen. When it
resumes, `LostToken` is cancelled almost immediately — but "almost immediately" starts
when the process starts running again, which may be well after a successor took over.
The lock delay below narrows this window; the fencing token is what closes it.

**Network partitions.** A partitioned instance stops being able to renew. Its local
watchdog cancels `LostToken` at `SessionTTL - LeaseSafetyMarginSeconds` measured
locally, which is *before* Consul invalidates the session at `SessionTTL`, which is in
turn before any successor may acquire. That last gap is Consul's lock delay, set by
`LockDelaySeconds` (default 15). With the defaults:

```
t=0s    last successful renewal
t=8s    local watchdog cancels LostToken     (TTL 10 - margin 2)
t=10s   Consul invalidates the session and releases the key
t=25s   the earliest a successor may acquire (+ lock delay 15)
```

That ordering is covered by an integration test against a real Consul. It holds as long
as the old leader is running; it is not a guarantee that survives the process being
frozen.

---

## What this does not guarantee

**A distributed lock alone does not give you mutual exclusion.** This is not specific to
Consul or to this library. The argument is Martin Kleppmann's, in
[*How to do distributed locking*](https://martin.kleppmann.com/2016/02/08/how-to-do-distributed-locking.html)
(2016), and it applies to every lock service, this one included:

> A client holding a lock can be paused — by a GC pause, a page fault, a scheduler
> preemption — for longer than the lock's lease. The lock expires, another client
> acquires it, and then the first client wakes up and, still believing it holds the
> lock, writes to the shared resource.

No amount of checking closes that window, because the paused process cannot check
anything while paused. The fix is not a better lock. The fix is that **the resource must
reject stale writers**, and to do that it needs a monotonically increasing token from
the lock — a *fencing token*.

Concretely, this library does **not** guarantee that:

- **Two instances never run leader work concurrently.** They can. During the pause
  window above, the old leader's `LostToken` is uncancelled and its code is running.
  What the library guarantees is that the old leader's writes carry a *lower*
  `FencingToken` than the new leader's.
- **`LostToken` fires before a successor acquires.** It fires before, in every case
  where this process is scheduled. That covers network partitions and Consul failures.
  It does not cover this process being frozen.
- **Anything at all, if you ignore `FencingToken`.** If your side effects do not carry
  the token and your resource does not compare it, you have an advisory lock and a
  race, and the library's other guarantees will not save you.
- **The fencing token orders anything beyond this one key.** It is the `ModifyIndex` of
  one KV entry. It says nothing about other keys, other services, or wall-clock time.
- **Anything about `DLeader.Consul.Messaging`.** That is a separate package with much
  weaker promises and its own README. Nothing on this page applies to it, which is most
  of why it was split out.

### If your resource cannot accept a fencing token

Some cannot — a third-party API with no conditional write, a filesystem, a shell
command. Your options, in descending order of safety:

1. Put something that *can* fence in front of it: a database row, a compare-and-set in
   Consul's own KV, an object-store conditional write.
2. Make the operation idempotent, so a duplicate run is harmless.
3. Accept the risk explicitly, size the pause window against `SessionTTL`, and write
   down that you did.

There is no fourth option where the lock alone makes it safe.

---

## When not to use this

- **You need consensus over state, not just a leader.** Use a replicated log
  (Raft directly, Kafka) or a database with the data in it. Leader election tells you
  *who*, never *what*.
- **You are already running Kubernetes.** A `Lease` object in `coordination.k8s.io`,
  via the client-go leader election or a .NET equivalent, gives you the same thing
  without another dependency. Its `Lease` also carries a version you can fence with.
- **You already run etcd or ZooKeeper and not Consul.** Both do this well. Adding a
  Consul cluster only for leader election is a lot of operational surface for one lock.
- **The work is short and idempotent.** If running it twice is harmless, a lock is
  ceremony. Make it idempotent and skip the coordination.
- **You need sub-second failover.** Consul's minimum session TTL is 10 seconds and the
  default lock delay adds 15 more. Worst-case failover here is tens of seconds, by
  design. Something with a heartbeat in the tens of milliseconds is a different tool.
- **You need mutual exclusion for correctness and cannot fence.** See the section
  above. This library will not give you what you need, and neither will any other lock.

---

## Configuration

| Option | Default | Meaning |
|---|---|---|
| `ServiceName` | *(required)* | Scopes the lock key: `service/{ServiceName}/leader`. |
| `Address` | `http://localhost:8500` | Consul agent HTTP address. |
| `AclToken` | *(empty)* | ACL token. Required on a cluster with ACLs enabled — without one every call is rejected. See below. |
| `Datacenter` | *(empty)* | Pins the expected datacenter so a misconfigured agent fails loudly. Does not enable cross-datacenter election, which is not possible here. |
| `SessionTTL` | `10` | Session lifetime in seconds. Consul enforces a minimum of 10. |
| `LockDelaySeconds` | `15` | Seconds Consul refuses the lock to anyone after an invalidation. The main safety/failover dial. `0` removes the guard. |
| `LeaseSafetyMarginSeconds` | `2` | Subtracted from `SessionTTL` to get the local deadline at which a lease declares itself lost. Must be `> 0` and `< SessionTTL`. |

Lowering `LockDelaySeconds` shortens failover and shortens the window in which a failed
leader is expected to notice. Raising it does the opposite. There is no setting that
gives you both.

### Running against a cluster with ACLs enabled

Set `AclToken`. The minimum policy the lease API needs:

```hcl
key_prefix "service/<name>/leader" { policy = "write" }
session_prefix ""                  { policy = "write" }
```

The campaign API additionally needs `service_prefix "<name>"` with write, and the
message broker needs `key_prefix "messages/<name>/"` with write.

The token is only applied to clients this library constructs. If you register your own
`IConsulClient`, configure the token on it yourself.

Bind from `appsettings.json` the usual way:

```csharp
builder.Services.AddConsulLeaderElection(
    consul => builder.Configuration.GetSection("Consul").Bind(consul));
```

---

## Observability

The library publishes an `ActivitySource` and a `Meter`, both named `DLeader.Consul`:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(LeadershipTelemetry.ActivitySourceName))
    .WithMetrics(m => m.AddMeter(LeadershipTelemetry.MeterName));
```

| Instrument | |
|---|---|
| `dleader.consul.leadership.lost` | Losses, tagged by `reason`. **The one to alert on.** |
| `dleader.consul.leadership.acquisitions` | Attempts, tagged `acquired` / `contended` / `failed`. |
| `dleader.consul.leadership.held` | 1 while this process holds a lease. Summed across a fleet it should never exceed one per service. |
| `dleader.consul.leadership.tenure` | How long each term lasted, in seconds. |
| `dleader.consul.session.renewals` | Renewals, tagged `ok` / `failed`. Rising failures with no loss means the margin is being eaten into. |
| `dleader.consul.leadership.fencing_token_issued` | Tokens issued. Alert on it going backwards — see the snapshot-restore assumption above. |

The `reason` tag is why this exists:

- `session_expired`, `lock_key_taken` — Consul made a decision and told this node.
- `local_deadline_exceeded` — this node could not reach Consul and stood down on its own
  clock. **This is what a network partition looks like from inside the process.** It is
  a different incident from the first two and deserves a different alert.

`ILeadershipLease.LostReason` exposes the same value in code, for callers that want to
react differently to a partition than to an orderly hand-off.

## Less boilerplate

`LeaderElectedService` is the loop above as a base class, for the common case of a
background service whose work should run on one instance:

```csharp
public sealed class InvoiceCloser : LeaderElectedService
{
    private readonly IInvoiceStore _store;

    public InvoiceCloser(ILeadershipLeaseProvider leases, ILogger<InvoiceCloser> logger, IInvoiceStore store)
        : base(leases, logger) => _store = store;

    protected override async Task ExecuteAsLeaderAsync(ILeadershipLease lease, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await _store.CloseBatchAsync(lease.FencingToken, ct);
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }
}
```

`ExecuteAsLeaderAsync` is called once per leadership term, with a token cancelled the
moment the term ends. It is a convenience with no path to Consul of its own — the
guarantees are the ones documented above, not a second set.

## Upgrading from 1.x

**2.0 removed `ILeaderElection` entirely**, along with the Consul service registration
that only it used, and moved `IMessageBroker` to
[`DLeader.Consul.Messaging`](https://www.nuget.org/packages/DLeader.Consul.Messaging).

Two APIs contending for the same lock key with different guarantees was the problem, not
the solution. `IsLeaderAsync` was deprecated in 1.11 and warned about for two releases
before it went.

[MIGRATION.md](MIGRATION.md) walks through the change. If you already moved to the lease
API on 1.11–1.13, 2.0 is a version bump.

---

## Development

```bash
dotnet build                                # net8.0, net9.0, net10.0
dotnet test DLeader.Consul.Tests            # unit, all targets
dotnet test DLeader.Consul.IntegrationTests # single Consul agent; needs Docker
dotnet test DLeader.Consul.ClusterTests     # three-server Consul; slow
```

Run the two suites separately. They are separate projects because sharing a run means a
dozen containers competing while Raft elections time out, which produced failures that
vanished when either suite ran alone.

Integration tests start real Consul containers through Testcontainers. They are the only
tests that can tell you whether the guarantees above hold — every bug fixed in 1.11.0 was
invisible to the mocked suite, and two more were found by these — so changes to the lease
or broker paths need to go through them.

Most run against a single agent and cover contested acquisition, fencing-token
monotonicity, loss detection when the session is destroyed, loss detection when the agent
becomes unreachable, ACL enforcement, message delivery and de-duplication, and what the
campaign path registers.

The `Cluster` category runs against **three Consul servers with real Raft**, and covers
quorum loss, Raft leader failover, and fencing-token monotonicity across a Consul
election. Those are the only tests that exercise what makes Consul strongly consistent —
a single dev agent has no quorum to lose — so they are what backs the assumptions table
above. They are slow by design: bootstrapping Raft and waiting out an election takes real
time and cannot be faked. CI runs them once rather than per target framework, since
nothing they test varies by framework.

Requires the .NET 10 SDK to build (it produces all three targets) and Docker to run the
integration suite.

## License

MIT — see [LICENSE](LICENSE).

## Maintainers

Franco Pachue
