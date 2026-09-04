# Migration guide

## From 1.10 to 1.13

Nothing was removed. Your code still compiles, and `IsLeaderAsync()` still returns what
it always did. What changed is that the compiler now warns about it, because the pattern
it forces cannot be made correct.

### Why `IsLeaderAsync` is obsolete

```csharp
if (await _leaderElection.IsLeaderAsync())   // true, as of a moment ago
{
    await DoTheWork();                        // leadership may already have moved
}
```

The `bool` describes the past. Between the check and the work, this instance's Consul
session can expire — a garbage-collection pause longer than `SessionTTL` is enough — and
another instance can be granted leadership. Both then run `DoTheWork()`.

Checking again inside the loop does not help. A paused process cannot check anything
while it is paused. This is not a bug in the implementation; it is what the signature
permits.

### The replacement

Hold a lease for the duration of the work, and pass its fencing token to whatever you
are protecting.

```csharp
public sealed class InvoiceWorker : BackgroundService
{
    private readonly ILeadershipLeaseProvider _leases;   // was ILeaderElection
    private readonly IInvoiceStore _store;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Waits on a blocking query rather than polling.
            await using var lease = await _leases.AcquireLeadershipAsync(stoppingToken);

            using var work = CancellationTokenSource.CreateLinkedTokenSource(
                lease.LostToken, stoppingToken);

            try
            {
                while (!work.IsCancellationRequested)
                {
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

### Step by step

**1. Change the injected dependency.**

```diff
- private readonly ILeaderElection _leaderElection;
+ private readonly ILeadershipLeaseProvider _leases;
```

Registration does not change: `AddConsulLeaderElection(...)` registers both interfaces
against one shared instance.

**2. Delete the `StartLeaderElectionAsync` call.** The lease API does not need it.
Acquisition is the whole protocol.

```diff
- await _leaderElection.StartLeaderElectionAsync(stoppingToken);
```

**3. Replace the check with a lease.**

```diff
- while (!stoppingToken.IsCancellationRequested)
- {
-     if (await _leaderElection.IsLeaderAsync())
-     {
-         await DoTheWork();
-     }
-     await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
- }
+ while (!stoppingToken.IsCancellationRequested)
+ {
+     await using var lease = await _leases.AcquireLeadershipAsync(stoppingToken);
+
+     using var work = CancellationTokenSource.CreateLinkedTokenSource(
+         lease.LostToken, stoppingToken);
+
+     try
+     {
+         while (!work.IsCancellationRequested)
+         {
+             await DoTheWork(lease.FencingToken, work.Token);
+             await Task.Delay(TimeSpan.FromSeconds(5), work.Token);
+         }
+     }
+     catch (OperationCanceledException) { }
+ }
```

Use `TryAcquireLeadershipAsync` instead of `AcquireLeadershipAsync` when a follower has
work of its own to do rather than idling; it returns `null` immediately instead of
waiting.

**4. Make the resource check the fencing token.** This is the step that actually buys
you anything, and the one it is tempting to skip.

```sql
UPDATE invoice_batches
   SET status = 'closed', fencing_token = @token
 WHERE id = @id
   AND fencing_token <= @token;
```

If the update affects no rows, a later leader has already been here and this instance is
stale — whatever its lease still believes.

Without this step you have an advisory lock and a race. See
[What this does not guarantee](README.md#what-this-does-not-guarantee).

**5. Replace the events, if you used them.**

`OnLeadershipAcquired` and `OnLeadershipLost` still work, but they hand the handler
nothing to fence with. The lease loop above covers both transitions: the body runs on
acquisition, and `work.Token` cancels on loss.

### Behaviour that changed in 1.11 and 1.12

These are fixes, but they are observable, so check them against your assumptions:

| Change | What you may notice |
|---|---|
| A lost session now raises `OnLeadershipLost` and rebuilds the session | Previously the node went silent and never led again. If you were restarting processes to recover from that, you can stop. |
| Startup no longer deregisters sibling instances of the same service | Set `ServiceRegistrationOptions.DeregisterSiblingInstancesOnStart = true` to restore the old behaviour. It was destructive when two instances shared a Consul agent. |
| `ServiceRegistrationOptions` is now honoured | Five of its six properties were silently ignored before. If you set `HealthCheckEndpoint`, `HealthCheckInterval`, `HealthCheckTimeout`, `DeregisterCriticalServiceAfter` or `Tags`, they take effect now. Check they say what you meant. |
| The health check address falls back to the machine name, not `localhost` | If you relied on the old fallback, set `ServiceRegistrationOptions.ServiceAddress` explicitly. |
| `GetCurrentLeaderAsync` returns empty while the lock is unheld | It used to return the last leader's id from a key nobody held. |
| The message broker no longer replays the retention window on every message | Handlers that were tolerating duplicates will see far fewer. |

### New settings worth reviewing

| Setting | Default | Why you might change it |
|---|---|---|
| `AclToken` | *(empty)* | Required if your cluster has ACLs enabled. Without it every call is rejected. |
| `LockDelaySeconds` | `15` | The main safety/failover dial. Lower means faster failover and a smaller window for a failed leader to notice. |
| `LeaseSafetyMarginSeconds` | `2` | Subtracted from `SessionTTL` to get the local deadline at which a lease declares itself lost. |

### Observability

1.13 publishes an `ActivitySource` and a `Meter`, both named `DLeader.Consul`:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(LeadershipTelemetry.ActivitySourceName))
    .WithMetrics(m => m.AddMeter(LeadershipTelemetry.MeterName));
```

The instrument to alert on is `dleader.consul.leadership.lost` and its `reason` tag:

- `session_expired` / `lock_key_taken` — Consul made a decision and told this node.
- `local_deadline_exceeded` — this node could not reach Consul and stood down on its own
  clock. **This is what a network partition looks like from inside the process**, and it
  is the one worth paging on.

`ILeadershipLease.LostReason` exposes the same value in code.

---

## From 1.13 to 2.0

If you followed the steps above, this is a version bump. If you did not, they are the
work — the deprecated API is now gone rather than warning.

### What was removed

| Removed | Replacement |
|---|---|
| `ILeaderElection` (the whole interface) | `ILeadershipLeaseProvider` |
| `IsLeaderAsync()` | Hold a lease; observe `LostToken` |
| `StartLeaderElectionAsync` | Nothing. Acquisition is the whole protocol. |
| `OnLeadershipAcquired` / `OnLeadershipLost` | `LeaderElectedService`, or the loop above |
| `ConsulLeaderElection.GetCurrentLeaderAsync` | Still there, still advisory only |
| Consul service registration, health checks, `ServiceRegistrationOptions` | Register the service yourself if you want it registered |
| `ConsulOptions.LeaderCheckInterval`, `RenewInterval`, `VerificationRetries`, `VerificationRetryDelay` | Nothing; they were campaign-only |
| `AddConsulLeadership` | `AddConsulLeaderElection` + `AddConsulMessaging` |
| `IMessageBroker` | The `DLeader.Consul.Messaging` package |

### Service registration is no longer done for you

The library used to register this instance as a Consul service with an HTTP health check,
because the campaign API needed it. The lease API does not, so it does not.

If you relied on those registrations for discovery, register them yourself — that is a
concern of your application, and one line of `IConsulClient.Agent.ServiceRegister`.
Nothing about leadership changes either way.

### If you used the events

```diff
- _leaderElection.OnLeadershipAcquired += StartWork;
- _leaderElection.OnLeadershipLost += StopWork;
- await _leaderElection.StartLeaderElectionAsync(stoppingToken);
```

```csharp
public sealed class Worker : LeaderElectedService
{
    public Worker(ILeadershipLeaseProvider leases, ILogger<Worker> logger)
        : base(leases, logger) { }

    protected override async Task ExecuteAsLeaderAsync(ILeadershipLease lease, CancellationToken ct)
    {
        // Runs once per leadership term. ct cancels when the term ends.
        while (!ct.IsCancellationRequested)
        {
            await DoWorkAsync(lease.FencingToken, ct);
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }
}
```

This is better than the events were, in the way that matters: `ExecuteAsLeaderAsync`
receives the lease, so it can pass `FencingToken` downstream. The events handed the
handler nothing to fence with.

### If you used the message broker

```bash
dotnet add package DLeader.Consul.Messaging
```

```diff
- using DLeader.Consul.Abstractions;
+ using DLeader.Consul.Messaging;

- builder.Services.AddConsulLeadership(configureConsul, configureService);
+ builder.Services.AddConsulLeaderElection(configureConsul);
+ builder.Services.AddConsulMessaging();
```

The type and its behaviour are unchanged. Both packages register the Consul client with
`TryAddSingleton`, so using both still means one connection.
