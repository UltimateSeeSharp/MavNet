# Missions

The mission protocol lets you upload, download, clear, and run an on-vehicle plan
(waypoints, geofence, or rally points) over MAVLink. MavNet implements the full
three-direction state machine — upload, download, clear — plus the
`MAV_CMD_MISSION_START` execute path on top of the existing `COMMAND_LONG`
infrastructure.

## Quick start

```csharp
using MavNet.PX4.Missions;
using MavNet.PX4.Vehicles;

await using var drone = await Drone.ConnectAsync(
    "udp://0.0.0.0:14550?rhost=127.0.0.1&rport=18570",
    TimeSpan.FromSeconds(30));

// Build a 4-waypoint loop around (47.0°, 8.5°) at 20 m AGL.
var mission = new[]
{
    MissionItem.Takeoff(altMeters: 20f),
    MissionItem.Waypoint(47.0010, 8.5000, 20f, hold: 2f, acceptanceRadius: 3f),
    MissionItem.Waypoint(47.0010, 8.5010, 20f, hold: 2f, acceptanceRadius: 3f),
    MissionItem.Waypoint(47.0000, 8.5010, 20f, hold: 2f, acceptanceRadius: 3f),
    MissionItem.ReturnToLaunch(),
};

drone.MissionItemReached += seq => Console.WriteLine($"reached waypoint {seq}");

var upload = await drone.UploadMissionAsync(mission);
if (!upload.IsAccepted) throw new Exception($"Upload failed: {upload.AckResult}");

await drone.ArmAsync();
var start = await drone.StartMissionAsync();
Console.WriteLine($"Mission started: {start.Result}");
```

## API surface

All nine methods live on `Vehicle` (so `Drone`, and any future `Boat` / `Plane`
inherit them):

| Method                                       | Wire flow                                                                              |
| -------------------------------------------- | -------------------------------------------------------------------------------------- |
| `UploadMissionAsync(items, ct)`              | `MISSION_COUNT` → loop(`MISSION_REQUEST_INT` → `MISSION_ITEM_INT`) → `MISSION_ACK`     |
| `DownloadMissionAsync(ct)`                   | `MISSION_REQUEST_LIST` → `MISSION_COUNT` → loop(`MISSION_REQUEST_INT` → `MISSION_ITEM_INT`) → our `MISSION_ACK(Accepted)` |
| `ClearMissionAsync(ct)`                      | `MISSION_CLEAR_ALL` → `MISSION_ACK`                                                    |
| `UploadFenceAsync` / `DownloadFenceAsync` / `ClearFenceAsync` | Same protocol, `MAV_MISSION_TYPE_FENCE`                                |
| `UploadRallyAsync` / `DownloadRallyAsync` / `ClearRallyAsync` | Same protocol, `MAV_MISSION_TYPE_RALLY`                                |
| `StartMissionAsync(firstItem, lastItem, ct)` | `COMMAND_LONG` with `MAV_CMD_MISSION_START`, confirms when HEARTBEAT shows `AUTO.MISSION` |
| `SetCurrentMissionItemAsync(seq, ct)`        | `COMMAND_LONG` with `MAV_CMD_DO_SET_MISSION_CURRENT`                                   |

## Timing and retries

Per the MAVLink spec:

- **1500 ms** default per-message timeout (initial sends, ACK waits).
- **250 ms** inner timeout for the item request/response loop.
- **5** retries before a transaction fails as `Timeout`.

All three are constructor-injectable on `MissionClient` so unit tests stay
sub-second. The `Vehicle`-level facade uses spec defaults.

## Out-of-sequence is not an error

The spec says: *"Out-of-sequence messages in mission upload/download are
recoverable, and are not treated as errors."* MavNet honors this:

- On the upload side, if the vehicle re-requests an earlier seq (or repeats one),
  we re-answer with that item rather than abort.
- On the download side, if an item arrives with the wrong seq, we drop it and
  let the watchdog re-request the expected one.

The only paths to a failed transaction are:

1. A non-`Accepted` `MISSION_ACK` (→ `Status.Rejected`, `AckResult` carries the
   specific `MAV_MISSION_RESULT`).
2. Retry budget exhausted (→ `Status.Timeout`).
3. Caller cancellation (→ `Status.Cancelled`).

Per spec, on rejection both ends return to idle and the on-vehicle mission is
unchanged.

## Mission types run independently

`MAV_MISSION_TYPE_MISSION`, `_FENCE`, and `_RALLY` are three parallel state
machines. `Vehicle` lazily builds one `MissionClient` per type. You can run
upload-mission, upload-fence, and upload-rally concurrently without interference
— each client filters inbound mission messages by its own `mission_type`.

```csharp
var t1 = drone.UploadMissionAsync(waypoints);
var t2 = drone.UploadFenceAsync(fence);
var t3 = drone.UploadRallyAsync(rally);
await Task.WhenAll(t1, t2, t3);
```

What you *cannot* do is run two transactions of the same type concurrently on
the same `Vehicle` — that throws `InvalidOperationException`. Single-flight is
per `MissionClient`, which is per type.

## Live progress (`MISSION_CURRENT` / `MISSION_ITEM_REACHED`)

When the vehicle broadcasts `MISSION_CURRENT` or `MISSION_ITEM_REACHED`, `Vehicle`
updates:

- `MissionCurrentSeq`, `MissionTotal` (or `-1` if unknown)
- `MissionState` — `Active` / `Paused` / `Complete` / `NotStarted` per the enum
- `MissionOpaqueId`, `FenceOpaqueId`, `RallyOpaqueId` — see *opaque ids* below
- `MissionReachedCount` — distinct reach events seen
- Fires the public `MissionItemReached` event with the reached seq

Both `MISSION_CURRENT` and `MISSION_ITEM_REACHED` call `FireStateChanged()`, so
throttled `SubscribeState` subscribers (e.g. a Blazor 2 Hz panel) pick up
progress without extra plumbing. The fields are mirrored on `DroneState` so the
typed-snapshot overload sees them.

## Opaque IDs (plan-change detection)

`MISSION_CURRENT` carries three `uint32` fields — `mission_id`, `fence_id`,
`rally_points_id` — that change whenever the on-vehicle plan changes.
`MISSION_ACK` on a successful upload returns the new `opaque_id`. Cache that
value after upload; if a future `MISSION_CURRENT` shows a different id, someone
else (another GCS, a CompanionComputer) has replaced the plan. `0` means the
vehicle either has no plan or doesn't support change-detection ids.

```csharp
var result = await drone.UploadMissionAsync(items);
if (result.IsAccepted)
{
    var ourPlanId = result.OpaqueId!.Value;
    drone.SubscribeState(() =>
    {
        if (drone.MissionOpaqueId != ourPlanId)
            Log("on-vehicle plan changed externally");
    }, StateRate.Hz(1));
}
```

## Threading

Identical to the rest of MavNet:

- Inbound mission events fire **on the receive thread**, synchronously.
- The `MissionItemReached` handler runs there.
- `MissionClient` watchdog continuations run on the `ThreadPool`. The internal
  lock makes the receive thread and the watchdog mutually exclusive, so neither
  can observe torn state.
- Subscriber exceptions are swallowed — a buggy handler can't break the receive
  loop or the throttler.

Long-running mission handlers should marshal off the receive thread themselves
(`Task.Run` for CPU work, `InvokeAsync(StateHasChanged)` for Blazor).

## Coordinate scaling

`MissionItem.X` and `MissionItem.Y` are `int32` — the raw wire shape of
`MISSION_ITEM_INT`. For global frames, that's `degrees × 1e7`; for local frames,
`metres × 1e4`. The static factories on `MissionItem` (`Waypoint`, `Takeoff`,
`Land`, `ReturnToLaunch`) accept doubles and do the scaling for you. If you
need a command MavNet doesn't have a factory for, use `MissionItem.ToInt1e7` on
your lat/lon.

## Testing

`MissionClient` is testable in isolation against a fake connection. See
`tests/MavNet.PX4.Tests/MissionClientTests.cs` for the full state-machine
coverage. The common pattern:

```csharp
var conn = new FakeMavlinkConnection();
using var client = new MissionClient(conn, 1, 1,
    defaultTimeout: TimeSpan.FromMilliseconds(50),
    itemTimeout:    TimeSpan.FromMilliseconds(30),
    maxRetries: 2);

var task = client.UploadAsync(items);
conn.RaiseMissionRequestInt(new MavId(1,1), new MissionRequestInt(0, 1, 1, MavMissionType.Mission));
// ... drive the state machine ...
conn.RaiseMissionAck(new MavId(1,1), new MissionAck(255, 190,
    MavMissionResult.MavMissionAccepted, MavMissionType.Mission, 0xDEADBEEFu));

var result = await task;
result.IsAccepted.Should().BeTrue();
```

## Probe demo

`examples/MavNet.Probe` includes a one-key mission demo: press `M` after the
drone has a GPS fix and it will upload a 6-item square mission around the current
position, arm, and start it. The console prints each `MISSION_ITEM_REACHED` as
it arrives.

```
dotnet run -c Release --project examples/MavNet.Probe -- "udp://0.0.0.0:14550?rhost=127.0.0.1&rport=18570"
```
