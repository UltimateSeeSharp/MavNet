# MavNet — Rate-Controlled State Subscriptions

Working notes for the `StateRate` / `StateSubscription` / `IStateObservable`
primitives added to MavNet.Core, and the throttler implementation on
`Vehicle` / `Drone`. Not polished docs — meant as the source for proper
documentation later.

This pass was driven by an Atlas-side problem (Blazor pages were subscribing
directly to `Drone.StateChanged` and saturating the render loop), but the fix
was clearly a MavNet concern: rate control belongs next to the event source,
not in every consumer.

---

## 1. What was added

### `MavNet.Core.StateRate` (`readonly struct`)

How often a state-change subscriber wants to be called. Three factories,
not a public constructor:

```csharp
StateRate.Raw                                  // every packet, no throttle
StateRate.Hz(double hz)                        // at most N deliveries / sec
StateRate.Every(TimeSpan interval)             // at most one / interval
```

Properties:

- `MinInterval` — `TimeSpan`. Zero for `Raw`.
- `IsRaw` — `MinInterval == TimeSpan.Zero`.
- Value semantics: `Equals`, `==`, `!=`, `GetHashCode` all behave on `MinInterval`.
- `ToString` returns `"raw"` or `"≥ N ms"` — used for diagnostics and logs only.

Validation:

- `Hz(hz)` throws `ArgumentOutOfRangeException` if `hz <= 0`.
- `Every(interval)` throws if interval is negative.

### `MavNet.Core.StateSubscription` (`sealed class : IDisposable`)

Named handle returned by `SubscribeState`. Dispose to unsubscribe — there's
no `-=` form.

- `Rate` — the `StateRate` it was created with.
- `IsActive` — `true` until `Dispose` is called.
- `Dispose` is **idempotent and thread-safe** (`Interlocked.Exchange(_disposed, 1)` gate).
- `Dispose` wraps the disposal callback in try/catch — **never throws**.
- Constructor is `public` so anything across assembly boundaries can construct one.

Designed as a named type rather than `IDisposable` directly so it can grow
later without breaking callers. Plausible additions: `ChangeRate(StateRate)`,
`Pause()`/`Resume()`, `FiredCount`, `Lagged` event. None added yet.

### `MavNet.Core.IStateObservable`

```csharp
public interface IStateObservable
{
    StateSubscription SubscribeState(Action handler, StateRate rate);
}
```

Single-purpose. Identity (`Id`, `Name`), commands, and snapshot types stay on
concrete vehicle types — they're not pulled up here because they're not
universal across "things that observe state."

### `MavNet.Core.IStateObservable<TState>`

```csharp
public interface IStateObservable<TState> : IStateObservable where TState : struct
{
    StateSubscription SubscribeState(Action<TState> handler, StateRate rate);
}
```

Generic for the typed snapshot overload — `TState` is per-vehicle-type. Drone
implements `IStateObservable<DroneState>`. Future Boat / Rover would
implement `IStateObservable<BoatState>` etc.

`where TState : struct` enforces immutable snapshot types (the snapshot is
a value passed by copy; no shared reference for the handler to mutate).

---

## 2. Throttler implementation (in `Vehicle.cs`)

`Vehicle` is the base class that owns the MAVLink subscriptions, telemetry
state, and the existing public `StateChanged` event. The throttler lives
here so every vehicle type inherits it for free.

### Data layout

```csharp
private readonly object _subsLock = new();
private List<ThrottledSub>? _throttledSubs;   // lazy — null when none
private Timer? _throttleTimer;                // lazy — null when none
private long _currentTimerPeriodTicks;        // for retarget shortcut
```

`ThrottledSub`:

```csharp
private sealed class ThrottledSub
{
    public required Action Handler { get; init; }
    public required long MinIntervalTicks { get; init; }
    public long LastFiredTicks;
    public int Dirty;
}
```

Allocated **only when at least one throttled subscription exists**. A
vehicle with only `Raw` subscribers (or none) pays nothing — no list, no
timer, no allocation.

### Subscribe path

```csharp
public StateSubscription SubscribeState(Action handler, StateRate rate)
```

Branches on `rate.IsRaw`:

- **Raw:** `StateChanged += handler;` direct event subscription. The
  returned `StateSubscription`'s dispose callback does `StateChanged -= handler`.
  No timer, no list entry, no throttle cost.

- **Throttled:** creates a `ThrottledSub`, adds it under `_subsLock`,
  calls `RetargetTimerLocked()` which (re-)creates or `Change`s the
  timer to fire at the fastest current `MinInterval`. Returns a
  `StateSubscription` whose dispose callback removes the entry from the
  list under the lock and, if the list is now empty, disposes the timer
  and nulls both fields.

### Dirty propagation

`FireStateChanged()` is called by every MAVLink message handler in
`Vehicle.cs` (OnHeartbeat, OnGlobalPosition, OnVfrHud, OnGpsRaw,
OnSysStatus, OnExtendedSysState).

```csharp
private void FireStateChanged()
{
    try { StateChanged?.Invoke(); } catch { /* never break the receive loop */ }

    if (_throttledSubs is null) return;
    lock (_subsLock)
    {
        if (_throttledSubs is null) return;
        for (int i = 0; i < _throttledSubs.Count; i++)
            Interlocked.Exchange(ref _throttledSubs[i].Dirty, 1);
    }
}
```

The raw event fires **synchronously** on the receive thread. The
throttled marking happens under the lock but is a tight loop (set a flag
per entry, no handler invocation).

### Tick path

```csharp
private void OnThrottleTick()
{
    ThrottledSub[] snapshot;
    lock (_subsLock)
    {
        if (_throttledSubs is null || _throttledSubs.Count == 0) return;
        snapshot = _throttledSubs.ToArray();
    }

    var nowTicks = DateTime.UtcNow.Ticks;
    foreach (var sub in snapshot)
    {
        if (Interlocked.CompareExchange(ref sub.Dirty, 0, 1) != 1) continue;
        if (nowTicks - Interlocked.Read(ref sub.LastFiredTicks) < sub.MinIntervalTicks)
        {
            // Too soon — re-flag and let a later tick fire it.
            Interlocked.Exchange(ref sub.Dirty, 1);
            continue;
        }
        Interlocked.Exchange(ref sub.LastFiredTicks, nowTicks);
        try { sub.Handler(); } catch { /* never throw from throttler */ }
    }
}
```

Three properties this gives you:

1. **No fire without an intervening packet.** `Dirty` is only set by
   `FireStateChanged`. A tick with `Dirty == 0` does nothing.
2. **Per-subscriber rate enforcement.** Each sub's `LastFiredTicks` and
   `MinIntervalTicks` are checked independently. Mixing a 10 Hz and a 2 Hz
   subscriber works: the fast one fires every 100 ms, the slow one fires
   every 500 ms, neither slows down the other.
3. **Dirty doesn't get lost.** If a subscriber's interval hasn't elapsed,
   the dirty flag is re-set so a later tick picks it up.

### Timer cadence (no hardcoded 50 ms)

`RetargetTimerLocked()` walks the sub list, picks the smallest
`MinIntervalTicks`, and `Change`s the timer to that period:

```csharp
long fastestTicks = long.MaxValue;
foreach (var s in _throttledSubs)
    if (s.MinIntervalTicks < fastestTicks) fastestTicks = s.MinIntervalTicks;
```

So: one subscriber at `Hz(2)` → timer ticks every 500 ms. Add a second at
`Hz(10)` → timer retargets to 100 ms (the fast one's rate). The slow one
still only fires every 5 ticks. Remove the fast one → timer retargets
back to 500 ms. Remove all throttled subs → timer disposes.

`_currentTimerPeriodTicks` is a shortcut to avoid calling `Timer.Change`
when the recomputed period hasn't changed.

### Disposal

`Vehicle.DisposeAsync` disposes the timer and clears the list:

```csharp
lock (_subsLock)
{
    _throttleTimer?.Dispose();
    _throttleTimer = null;
    _throttledSubs = null;
}
```

Any outstanding `StateSubscription` objects are now inert — their dispose
callbacks find `_throttledSubs == null` and return early. Safe.

---

## 3. Snapshot type and typed overload (on `Drone`)

### `DroneState` (`readonly record struct`)

In `MavNet.PX4.Vehicles.Drone.cs`:

```csharp
public readonly record struct DroneState(
    double Lat, double Lon, double Alt, double Hdg, double Vel,
    string Mode, bool Armed, double Battery, int Sats, bool LinkUp);
```

Captured by `Drone.Snapshot()` which reads the underlying `Vehicle`
properties. Used by `Action<DroneState>` handlers.

### Typed `SubscribeState` overload

```csharp
public StateSubscription SubscribeState(Action<DroneState> handler, StateRate rate)
    => SubscribeState(() => handler(Snapshot()), rate);
```

Just wraps the no-payload overload with a snapshot-at-fire-time call. One
allocation per fire (the `DroneState` value plus the closure). No new
throttling logic — fully delegates to the base.

### When to use which

- `Action handler` — UI render triggers, fleet aggregators. Handler
  re-reads drone properties. **No allocation per fire** (the handler is
  closed over `_drone` or similar).
- `Action<DroneState> handler` — telemetry recording, replay, alerting,
  anything where the handler might run on a different thread from a later
  packet update. The snapshot avoids torn reads across separate property
  gets at the cost of one struct allocation per fire.

---

## 4. Public surface left intact

### `Vehicle.StateChanged` event

Still public. Doc-comment now reads:

```
Fires after every state-affecting message — high frequency (10–50 Hz typical).
Prefer SubscribeState(Action, StateRate): it lets you pick a throttle rate
at the call site (StateRate.Hz(2), StateRate.Every(...), or StateRate.Raw
for full fidelity). This raw event remains public for consumers that
genuinely want every packet without going through the throttler.
```

The two paths converge: `SubscribeState(handler, StateRate.Raw)` does
`StateChanged += handler` internally. So the event is technically
redundant for new consumers — kept public so existing code (and downstream
projects we don't see) doesn't break.

### Everything else on `Vehicle` / `Drone`

Unchanged. Commands (`ArmAsync`, `TakeoffAsync`, …), telemetry
properties (`Lat`, `Lon`, …), `LinkUp`, `ConnectionString`, `Id`, `Name`,
`SystemId`, `ComponentId`, `HeartbeatReceived` event — all unchanged.

---

## 5. Thread model — important for consumers to understand

### Where things run

- **Receive thread** — MAVLink message handlers (`OnHeartbeat`,
  `OnGlobalPosition`, etc.) run here. `FireStateChanged` is called from
  here. The raw `StateChanged` event fires **on this thread**, synchronously.
- **`ThreadPool`** — the throttle Timer dispatches its callback on a
  thread pool thread. Throttled `SubscribeState` handlers run there.

So a `SubscribeState(handler, StateRate.Raw)` handler runs on the receive
thread. A `SubscribeState(handler, StateRate.Hz(2))` handler runs on
ThreadPool. **The same handler signature has different threading depending
on the rate.** Document this in real docs.

### Blocking the receive thread

Raw handlers MUST be fast. If a raw handler blocks (e.g. calls `await
Task.Delay`, holds a lock, does sync I/O), it blocks the receive thread
and stalls MAVLink message processing for that vehicle.

Throttled handlers are safer — they run on the pool — but should still be
fast or `async void` themselves.

### Field-level atomicity in `Vehicle`

The telemetry fields are plain doubles / shorts / etc. with no
synchronization. Reads are eventually-consistent. For the UI rendering use
case this is fine. For recording / safety logic, **use the typed snapshot**
— `Drone.Snapshot()` does the reads, and `DroneState` is then immutable.

There's still no guarantee that the snapshot's `Lat` and `Lon` come from
the same packet — they may straddle a single packet update, since the
underlying fields are written field-by-field on the receive thread without
a lock. This is an existing MavNet property, not new. Acceptable for the
current Atlas use case. If a future consumer needs true atomic snapshots,
add a sequence-lock or a swap-in-record pattern in `Vehicle.cs`.

### Subscriber exception isolation

Subscriber exceptions are **swallowed**:

- Inside `FireStateChanged` around `StateChanged?.Invoke()`.
- Inside `OnThrottleTick` around `sub.Handler()`.
- Inside `StateSubscription.Dispose` around the disposal callback.

The receive loop and the throttler must never break from a subscriber
fault. Subscribers that need to know about their own errors must
try/catch inside themselves.

---

## 6. Edges and assumptions

- **No upper bound on throttle rate.** `StateRate.Hz(1000)` is accepted
  and the timer will run at 1 ms. Whether your hardware can deliver
  packets that fast is a separate question — the throttler won't fire if
  no packet has arrived (no `Dirty` set).
- **No lower bound either.** `StateRate.Every(TimeSpan.FromMinutes(10))`
  is legal. Timer fires every 10 minutes; subscriber gets called at most
  that often.
- **`StateRate.Raw` short-circuits.** `MinInterval == TimeSpan.Zero` is
  detected by `IsRaw`. No path through the throttler for raw subs. They
  cost the same as a hand-written `+= StateChanged`.
- **`Timer.Change` is non-blocking and uses the running timer.** It
  doesn't reset the in-flight callback; if a tick is mid-execution, it
  finishes before the new period takes effect. Acceptable.
- **`Timer` callbacks can overlap if a tick takes longer than the
  period.** With reasonable handlers this is unlikely; if it happens, two
  `OnThrottleTick` invocations may race. The interlocked operations
  protect per-sub state but the **snapshot inside the lock** is the
  invariant — each tick gets its own snapshot, and the `LastFiredTicks`
  check prevents double-firing within the interval. No re-entry guard
  beyond that.
- **`_throttledSubs.ToArray()` allocates per tick.** Tiny — fine for
  reasonable subscriber counts (< ~100). For high-fanout scenarios, switch
  to a `ConcurrentBag` or copy into a pooled buffer.
- **Disposal idempotency comes from `StateSubscription`, not the
  callback.** The `Action _onDispose` passed to the constructor may be
  called multiple times if you wrap it manually — but the public
  `Dispose()` method guards that. Don't invoke `_onDispose` directly.

---

## 7. Conventions for consumers

- **Use the named factories**, not raw `TimeSpan`s. `StateRate.Hz(2)`
  reads better than `new StateRate(TimeSpan.FromMilliseconds(500))` and
  the constructor is private anyway.
- **Hold the `StateSubscription` for the lifetime of your subscription**,
  dispose when done. Pattern in Blazor / `IDisposable` consumers:
  ```csharp
  private StateSubscription? _sub;
  protected override void OnInitialized() {
      _sub = _drone.SubscribeState(OnTick, StateRate.Hz(2));
  }
  public void Dispose() {
      _sub?.Dispose();
      _sub = null;
  }
  ```
- **Don't subscribe to `StateChanged` directly** unless you specifically
  need every-packet semantics on the receive thread. Prefer
  `SubscribeState(handler, StateRate.Raw)` — same effect, consistent API,
  natural `IDisposable` cleanup.
- **Use the typed overload when threading matters.** UI render triggers
  can use the no-payload form. Telemetry recorders, replay capture, and
  anything that crosses thread boundaries should take the snapshot.
- **Marshal to your own thread inside the handler** if needed. For Blazor:
  `await InvokeAsync(StateHasChanged)`. The throttler does not do this
  for you.

---

## 8. Things explicitly NOT done (design space left open)

- **No `IObservable<T>` / Rx support.** Considered, rejected. Adds a
  500 KB dependency for what is a single-method primitive. An opt-in
  `MavNet.Reactive` package could add `vehicle.AsObservable()` later —
  sourced from `SubscribeState(handler, StateRate.Raw)` feeding a
  `Subject<T>`. Not built.
- **No `StateSubscription.ChangeRate`.** Caller currently disposes and
  re-subscribes. Adding `ChangeRate` would let a handler stay live while
  the rate changes — useful for "snappier when focused, slower when not."
  Not built.
- **No `StateSubscription.Pause()`/`Resume()`.** Same — caller disposes
  and re-subscribes. Could be added if Atlas (or another consumer) finds
  the dispose-and-resubscribe pattern noisy.
- **No `FiredCount` / `Lagged` event on `StateSubscription`.** Diagnostic
  hooks. Not needed yet.
- **No backpressure metric inside the throttler.** If a throttled
  subscriber's handler is so slow that the next tick fires while the
  previous is still running, there's no signal today. Could add a
  "skipped because in-flight" counter.
- **No per-vehicle-type interface beyond `IStateObservable<TState>`.**
  When/if Boat, Rover, Plane are added, the question of a common
  `IVehicle` interface arises. Not built — composition (each vehicle
  implements the interfaces it actually satisfies) is preferred over a
  kitchen-sink base.
- **No true atomic snapshot.** `Drone.Snapshot()` reads fields one at a
  time. For multi-field consistency under high-rate packet updates,
  consider a swap-in-record pattern in `Vehicle.cs` — but that's a deeper
  change, and it's not needed for current Atlas use.

---

## 9. Files changed in this pass

### Added
- `src/MavNet.Core/StateRate.cs`
- `src/MavNet.Core/StateSubscription.cs`
- `src/MavNet.Core/IStateObservable.cs` (both interfaces in one file)

### Modified
- `src/MavNet.PX4/Base/Vehicle.cs`
  - Implements `IStateObservable`.
  - Adds throttler state fields, `ThrottledSub` private class,
    `SubscribeState(Action, StateRate)`, `OnThrottleTick`,
    `RetargetTimerLocked`.
  - `FireStateChanged` extended to mark throttled subs dirty.
  - `DisposeAsync` disposes the throttle timer and clears the list.
  - Doc-comment on `StateChanged` event nudges toward `SubscribeState`.
- `src/MavNet.PX4/Vehicles/Drone.cs`
  - Adds `DroneState` `readonly record struct` at file top.
  - `Drone` implements `IStateObservable<DroneState>`.
  - Adds `Snapshot()` method and the typed `SubscribeState` overload.

### Unchanged
- Everything in `MavNet.Protocol`, `MavNet.Protocol.Generated`,
  `MavNet.Transport.Udp`. The transport and protocol layers don't know
  about state observation — that's a `Vehicle`-layer concern.
- `MavNet.Core.MavId` — unchanged.

---

## 10. How a consumer ports old code

### Old (still works, but doc-deprecated for periodic use)

```csharp
_drone.StateChanged += OnStateTick;
// ...
_drone.StateChanged -= OnStateTick;
```

### New, equivalent

```csharp
_sub = _drone.SubscribeState(OnStateTick, StateRate.Raw);
// ...
_sub.Dispose();
```

### New, throttled

```csharp
_sub = _drone.SubscribeState(OnStateTick, StateRate.Hz(2));
// ...
_sub.Dispose();
```

### New, typed snapshot (recommended for cross-thread / recording)

```csharp
_sub = _drone.SubscribeState(state => {
    _log.Info("lat={lat} lon={lon}", state.Lat, state.Lon);
}, StateRate.Hz(1));
// ...
_sub.Dispose();
```

For Atlas's `VehicleRegistry`, the current pattern is:

```csharp
var subscription = drone.SubscribeState(RaiseChanged, StateRate.Hz(2));
// stored on the registry's per-vehicle Entry record
// disposed on DisconnectAsync / DisposeAsync
```

For Atlas's `DroneDetailPanel.razor`, the current pattern is:

```csharp
private static readonly StateRate PanelRate = StateRate.Hz(5);
// ...
_stateSub = _drone.SubscribeState(OnTelemetry, PanelRate);
```
