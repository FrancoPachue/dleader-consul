# DLeader.Consul.Messaging

Best-effort message fan-out between the instances of a service, carried over the
HashiCorp Consul KV store. Split out of [DLeader.Consul](https://www.nuget.org/packages/DLeader.Consul)
in 2.0, where it was deprecated in 1.13.

```bash
dotnet add package DLeader.Consul.Messaging
```

```csharp
builder.Services.AddConsulMessaging(consul =>
{
    consul.ServiceName = "invoice-worker";
    consul.Address     = "http://consul:8500";
});
```

```csharp
await broker.SubscribeAsync("cache-invalidate", async key =>
{
    _cache.Remove(key);
});

await broker.BroadcastAsync("cache-invalidate", "customer:42");
```

## What this is not

**It is not a queue**, and no setting makes it one.

- A subscriber receives what is published after `SubscribeAsync` completes — not before.
- Messages are swept after `MessageBrokerOptions.Retention` (five minutes by default).
  An instance that is down for longer never sees what it missed. Raising the retention
  widens the window; it does not make delivery reliable.
- Delivery is normally exactly once per subscriber and in the order Consul accepted the
  messages, but a retry after a failed watch can redeliver. Handlers must tolerate that.
- There is no acknowledgement, no dead-letter, no replay, and no durability.

It suits coordination chatter — "the cache changed", "reload your config" — where
missing a message is survivable because the next one supersedes it. Anything that must
not be lost belongs in a real broker.

## Why it was split out

It has nothing to do with leader election, and shipping a non-durable fan-out inside a
package that promises leadership guarantees invited it to be mistaken for something it
is not.

## Options

| Option | Default | Meaning |
|---|---|---|
| `Retention` | `5 min` | How long a message survives before the sweep deletes it. |
| `CleanupInterval` | `1 min` | How often expired messages are swept. |
| `WatchTimeout` | `1 min` | Long-poll timeout. Not a delivery delay — Consul answers as soon as something changes. |

Consul connection settings (`Address`, `AclToken`, `Datacenter`, `ServiceName`) come
from `ConsulOptions`, shared with the leadership package.

## License

MIT
