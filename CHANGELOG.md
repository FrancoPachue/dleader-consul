# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.11.0] - 2026-09-03

The release that makes the guarantees explicit. Nothing public was removed or changed
shape, so this is a minor version, but the safety story is different: there is now an
API that can be used correctly, and the one that could not is marked obsolete.

### Added

- `ILeadershipLeaseProvider.TryAcquireLeadershipAsync` and `ILeadershipLease`, a
  leadership claim that stays valid for the duration of the caller's work. The lease
  exposes:
  - `FencingToken` — the Consul `ModifyIndex` of the lock key at acquisition. Derived
    from the Raft log index, so every successive leader sees a strictly greater value.
    Pass it to the resource being protected and have that resource reject lower values;
    this is the only thing that makes concurrent access actually safe.
  - `LostToken` — cancelled when the session expires, when the lock key is observed in
    another session's hands, when the local deadline passes without a successful
    renewal, or on disposal.
  - `IAsyncDisposable` — releases the lock against this instance's own session and
    destroys it, so a successor does not have to wait out the TTL.
- `ConsulOptions.LockDelaySeconds` (default 15) — seconds Consul refuses the lock to
  anyone else after invalidating the holding session. The main safety/failover dial.
- `ConsulOptions.LeaseSafetyMarginSeconds` (default 2) — subtracted from `SessionTTL`
  to derive the local deadline at which a lease declares itself lost. Measured on a
  monotonic clock, so wall-clock skew cannot move it.
- `MessageBrokerOptions` — `Retention`, `CleanupInterval` and `WatchTimeout` for the
  message broker, previously hardcoded.
- `ServiceRegistrationOptions.ServiceAddress` — the address Consul dials for the health
  check, for when it differs from the machine name.
- `ServiceRegistrationOptions.DeregisterSiblingInstancesOnStart` — off by default; see
  Fixed.
- `ConsulMessageBroker` implements `IAsyncDisposable`.
- Integration test suite running against a real Consul in a container via
  Testcontainers: contested acquisition, fencing-token monotonicity across handovers,
  loss detection when the session is destroyed, loss detection when the agent becomes
  unreachable, and recovery of the campaign API after session loss.
- XML documentation is now generated and shipped with the package.

### Changed

- **Targets `net8.0`, `net9.0` and `net10.0`.** `Microsoft.Extensions.*` is referenced
  per target framework rather than pinned across all of them, so consumers on `net8.0`
  are not dragged onto the 10.x assemblies.
- Updated `Consul` to 1.8.0. That release adds single-argument overloads to
  `IKVEndpoint` and `ISessionEndpoint`, which silently changes overload resolution for
  calls that relied on the optional `CancellationToken`. Every Consul call in the
  library now passes its token explicitly.
- `TreatWarningsAsErrors`, deterministic builds and `ContinuousIntegrationBuild` on CI
  are enabled repo-wide. The build went from 69 warnings to zero.
- CI now runs on every push and pull request, not only on release tags, and publishing
  to NuGet is gated on those runs passing. The tag is checked against the project
  version before publishing, so a tag that disagrees with `<Version>` fails the release
  rather than shipping a surprise.
- The sample application uses the lease API throughout.

### Deprecated

- `ILeaderElection.IsLeaderAsync()` is `[Obsolete]`. Its signature is a check-then-act
  race: a `bool` describing the past is stale before the caller can act on it, so work
  guarded only by it can run on two instances at once. Use
  `ILeadershipLeaseProvider.TryAcquireLeadershipAsync`. The method still works and the
  rest of `ILeaderElection` is untouched.

### Fixed

- **A lost session left the node permanently wedged, believing it still led.** Consul
  answers an acquire against an invalidated session with HTTP 500, which surfaces as an
  exception; the election loop's catch-all swallowed it and jumped over the branch that
  lowers the leader flag. `OnLeadershipLost` never fired, the session was never
  rebuilt, and the node both believed it led while a successor did and could never lead
  again. The loop now recognises an invalid session, raises `OnLeadershipLost`, and
  recreates the session.
- **Disposal could take the lock away from the current leader.** `DisposeAsync` issued
  an unconditional `KV.Delete` on the lock key whenever it believed it was the leader —
  including when that belief was stale, in which case it deleted a successor's lock.
  It now issues a session-scoped `KV.Release`, which Consul ignores if the lock has
  moved on.
- **`Dispose()` released nothing.** The synchronous path only cancelled a token: no
  deregistration, no lock release, and it never marked the instance disposed. It now
  performs the same cleanup as `DisposeAsync`, off the thread pool so it cannot
  deadlock against an ambient synchronization context, and bounded by a timeout.
- **`DisposeAsync` could hang forever.** The internal `CancellationTokenSource` was
  never connected to the background loops, which observed only the caller's token.
  Disposal cancelled that source and then awaited loops nothing had told to stop. The
  sources are now linked.
- **`AddConsulLeadership()` with no arguments produced an unusable container.**
  `IOptions<ConsulOptions>` was only registered when a configuration delegate was
  supplied, so resolving `ILeaderElection` threw `InvalidOperationException`.
- **`GetCurrentLeaderAsync` could name an instance that was no longer the leader.** It
  returned the value on the lock key without checking whether any live session held it.
  It now returns an empty string when the key is unheld, and tolerates a null value.
- `_isLeader` is written by the election loop and read during disposal on another
  thread; it is now `volatile`.
- The message broker no longer disposes the injected `IConsulClient`, which is a
  singleton shared with the leader election.
- Fixed a compilation error in the message broker's watch loop
  (`GetValueOrDefault` could not infer `ulong` from an `int` literal).
- **The broker replayed the whole retention window on every message.** The watch loop
  re-dispatched every key still under the prefix whenever any of them changed, so
  publishing N messages produced O(N²) handler invocations and each handler saw every
  message it had already processed again. It now tracks the index each key was last
  modified at and dispatches only what is above the last one seen, in `ModifyIndex`
  order.
- **Messages published in the same timer tick overwrote each other.** Keys were built
  from `DateTime.UtcNow.Ticks` alone, whose resolution is around 15 ms on Windows, so a
  tight publish loop silently lost messages. Keys now carry a per-instance sequence and
  a unique suffix.
- **`SubscribeAsync` returned before the watch existed.** Anything published in that
  window was dropped, because the watch then started from an index that already
  included it. The returned task now completes only once the subscription is
  established, so "published after `SubscribeAsync` returns" means something.
- **The broker disposed its `CancellationTokenSource` while the watch loops were still
  using its token.** Only the cleanup loop was awaited, and only for 500 ms. All loops
  are now awaited before the source goes away.
- A new subscriber no longer receives the backlog of messages published before it
  existed.
- **Startup deregistered sibling instances sharing a Consul agent.** `Agent.Services`
  lists everything on the local agent, so with two instances of a service on one host
  each removed the other on startup. This is now opt-in through
  `DeregisterSiblingInstancesOnStart` and off by default: re-registering an existing id
  already replaces the previous incarnation, and Consul removes registrations whose
  health check has been failing for `DeregisterCriticalServiceAfter`.
- **Five of the six `ServiceRegistrationOptions` properties were ignored.** The service
  registration hardcoded the tags, health endpoint, check interval, check timeout and
  deregister window, so setting `HealthCheckEndpoint` to `/healthz` changed nothing.
  All of them are now honoured.
- **The health check could be registered against `localhost`.** The address fell back
  to the literal `localhost` while the instance id fell back to the machine name, so
  the two disagreed and Consul checked whichever host its agent ran on. Both now fall
  back the same way, and `ServiceAddress` overrides it.

## [1.10.0] - 2025-02

Earlier releases are not documented here. The release tag was `v1.10` and the workflow
derived the package version from it, which NuGet normalised to `1.10.0` — so the
published version is correct. The project file, however, still said `1.0.0`, which is
fixed in 1.11.0 along with a CI check that the tag and `<Version>` agree.

[Unreleased]: https://github.com/FrancoPachue/dleader-consul/compare/v1.11.0...HEAD
[1.11.0]: https://github.com/FrancoPachue/dleader-consul/compare/v1.10...v1.11.0
[1.10.0]: https://github.com/FrancoPachue/dleader-consul/releases/tag/v1.10
