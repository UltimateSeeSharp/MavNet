# MavNet Roadmap

Path to a "full" C# MAVLink SDK. This document is the single source of truth for what is done, what is in flight, and what comes next.

## Goals

- **Primary autopilots:** PX4 first-class today; ArduPilot first-class next (dialect-agnostic core).
- **Primary consumer:** GCS / ground-side apps (Blazor / WPF / console). Threading-friendly, mission-download-capable, log-download-capable.
- **v1.0 scope:** Full MAVLink protocol surface — transports, params, missions, FTP, signing, routing, camera, gimbal, gripper, winch, logging.

---

## Current state (baseline)

| Area | Status | Evidence |
|---|---|---|
| Wire format v2, CRC, truncation, forward-compat drop | Done | `src/MavNet.Protocol/MavlinkFrame.cs`, `Crc16.cs` |
| Codegen (spec.xml -> records, enums, MAV_CMD wrappers) | Done, single-dialect | `tools/MavNet.CodeGen/`, `specs/common.xml` |
| UDP transport + heartbeat timer + typed events | Done | `src/MavNet.Transport.Udp/MavlinkConnection.cs` |
| Static-abstract zero-alloc send path | Done | `MavlinkConnection.Send<T>` |
| Vehicle facade with COMMAND_LONG / COMMAND_ACK correlation | Done (PX4) | `src/MavNet.PX4/Base/Vehicle.cs`, `Vehicles/Drone.cs` |
| Mission protocol: upload / download / clear / start, waypoints + fence + rally, MISSION_CURRENT + MISSION_ITEM_REACHED | Done | `src/MavNet.PX4/Missions/MissionClient.cs`, `Base/Vehicle.cs`, `docs/articles/missions.md` |
| Rate-controlled state subscription | Done | `MavNet.Core/IStateObservable` |
| Test harness (xUnit + FluentAssertions), CI matrix, codegen-drift check | Done | `tests/`, `.github/workflows/ci.yml` |
| NuGet + SourceLink + deterministic builds | Done | `Directory.Build.props` |
| Docs site (DocFX) with architecture + getting-started | Thin | `docs/articles/` |

**Allowlisted messages today (17):** HEARTBEAT, COMMAND_LONG / ACK, GLOBAL_POSITION_INT, VFR_HUD, GPS_RAW_INT, SYS_STATUS, EXTENDED_SYS_STATE, and the 9 MISSION_* messages (REQUEST_LIST, COUNT, CLEAR_ALL, ITEM_REACHED, ACK, CURRENT, REQUEST, REQUEST_INT, ITEM_INT).

---

## Milestones

Each milestone is a thin vertical slice: code + tests + docs, NuGet-shippable.

### M1 — Mission protocol (Done)

Full waypoint / fence / rally upload + download + clear + start state machines on `MissionClient`, surfaced via `Vehicle.{Upload,Download,Clear}{Mission,Fence,Rally}Async` and `Vehicle.StartMissionAsync`. Live progress (`MissionCurrentSeq`, `MissionTotal`, `MissionState`, `MissionReachedCount`, opaque ids) on `Vehicle`. `MissionItemReached` event. State-machine coverage against `FakeMavlinkConnection` in `tests/MavNet.PX4.Tests/MissionClientTests.cs` and `VehicleMissionFacadeTests.cs`. Docs: `docs/articles/missions.md`. Probe demo: press `M` after GPS fix to upload a square mission and start it.

### M2 — Parameter Protocol

Without this the SDK is read-only.

- Allowlist `PARAM_REQUEST_LIST`, `PARAM_REQUEST_READ`, `PARAM_VALUE`, `PARAM_SET`. Regenerate, wire dispatcher events (CLAUDE.md "Code generation" 4-step ritual).
- New `src/MavNet.PX4/Params/ParameterClient.cs` — full-list fetch with progress, single-param read, `PARAM_SET` with value-echo confirmation and timeout (model after `PendingCommand`).
- In-memory `ParameterCache` keyed by string id, exposed via `Drone.Parameters`.
- Handle `PARAM_VALUE.param_index == UInt16.MaxValue` (unknown) and the float-reinterpret for non-FLOAT types.
- Tests: full-list with simulated packet loss via `FakeMavlinkConnection`, set+echo, set-rejection, type-coercion roundtrip.
- Docs: `docs/articles/parameters.md`.

**Exit:** `await drone.Parameters.FetchAllAsync()` and `await drone.Parameters.SetAsync("MIS_TAKEOFF_ALT", 5.0f)` work.

### M3 — Transport pluralism: TCP, Serial, multi-link

GCS users connect over USB/UART (telemetry radio) and TCP (SITL bridge) at least as often as UDP.

- Extract `IMavlinkConnection` and the frame-decode-dispatch loop into a new `MavNet.Transport` shared assembly (the dispatch switch is byte-source-agnostic).
- New `MavNet.Transport.Tcp/MavlinkTcpConnection.cs` — client and server modes.
- New `MavNet.Transport.Serial/MavlinkSerialConnection.cs` — wraps `System.IO.Ports.SerialPort`; parameterized baud / parity / stop.
- Extend `ConnectionString` to parse `tcp://host:port`, `serial:COM3?baud=57600`. URL-driven factory `MavlinkConnection.Open(string uri)`.
- Tests: TCP loopback echo, serial via socat/com0com pipe pair (skip on CI if not available), URI parser round-trip.
- Docs: `docs/articles/transports.md`.

**Exit:** the same Vehicle facade works against UDP, TCP, and serial unchanged.

### M4 — Dialect-agnostic core + ArduPilot

Today's codegen is single-dialect-per-build; that is a ceiling.

- Add `specs/ardupilotmega.xml`, `specs/development.xml`. The ardupilot spec `<include>`s common — codegen needs to resolve includes (one-day task in `tools/MavNet.CodeGen/`).
- Multi-dialect emission: emit into per-dialect sub-namespaces (`MavNet.Protocol.Generated.Common`, `.ArduPilot`) so msgid collisions don't matter. `MessageRegistry` becomes a merged lookup.
- Allowlist split per dialect.
- New `src/MavNet.ArduPilot/` project — `Copter`, `Plane`, `Rover` facades mirroring the PX4 ones; differences: ArduPilot uses `MAV_CMD_DO_SET_MODE` with custom_mode enums; mode strings live in ardupilotmega-only enums.
- Tests: ardupilotmega message roundtrips, Copter arm/takeoff against ArduPilot SITL.
- Docs: `docs/articles/dialects.md`, `docs/articles/ardupilot.md`.

**Exit:** the same `IMavlinkConnection` drives PX4 `Drone` and ArduPilot `Copter`.

### M5 — Telemetry stream control + link health

GCS apps must throttle telemetry per-message.

- Allowlist `MESSAGE_INTERVAL`, `SET_MESSAGE_INTERVAL` (modern), `REQUEST_DATA_STREAM` (legacy ArduPilot).
- `Drone.RequestStream(msgid, hz)` helper that picks the modern path for PX4 and legacy for ArduPilot pre-4.0.
- Promote the existing heartbeat-timeout watcher (`Vehicle.LinkUp`) to a proper `LinkHealth` record: rtt estimate from heartbeat cadence, packet-loss counter from sequence gaps (sequence tracking lives in `MavlinkConnection`).
- Tests: rate-change applied via fake connection, sequence-gap loss counter, link-down event.
- Docs: extend `docs/articles/telemetry.md`.

**Exit:** `drone.Telemetry.SetRate<GlobalPositionInt>(5.0)` works on both stacks; `drone.LinkHealth.PacketLossRate` is meaningful.

### M6 — File Transfer Protocol + log download

Dataflash log download is a defining GCS feature, and FTP is the underlying transport.

- Allowlist `FILE_TRANSFER_PROTOCOL` (msgid 110). The FTP opcode/error enums (`MavFtpOpcode`, `MavFtpErr`) already generate.
- New `src/MavNet.Ftp/MavlinkFtpClient.cs` — session-id management, sequence numbers, all opcodes (OpenFileRO/WO, ReadFile, WriteFile, ListDirectory, CreateFile, RemoveFile, Rename, CalcFileCRC32, BurstReadFile). Burst-read is non-negotiable for download speed.
- `Drone.DownloadLogAsync(index, destPath, IProgress<long>)` on top of FTP.
- TLOG writer (`MavNet.Logging.TlogWriter`) that taps `MavlinkConnection`'s receive loop and writes the 8-byte-timestamp + raw-frame TLOG format. TLOG reader for replay-from-file.
- Tests: FTP read of synthetic file via fake connection, burst-read sequence-gap retry, TLOG roundtrip, TLOG-as-replay-source playing back through the dispatcher.
- Docs: `docs/articles/ftp.md`, `docs/articles/logging.md`.

**Exit:** `await drone.DownloadLogAsync(0, "flight.bin")` works against PX4 SITL; TLOG replay drives unit tests for everything else.

### M7 — Signing (HMAC-SHA256) + routing / bridging

GCS bridging (UDP <-> serial radio link) is common, and signed frames must stop being silently dropped.

- Re-enable signed-frame handling in `MavlinkFrame.TryDecode` (currently drops on `incompat & 0x01` per CLAUDE.md "Frame decode invariants"): parse 13-byte signature suffix, verify HMAC-SHA256, enforce monotonic 48-bit timestamp.
- `SigningKey` type, per-link key storage, `MavlinkConnection.SigningPolicy` (`Off` | `RequireOnRx` | `SignOnTx` | `Both`).
- `MavNet.Routing.MessageRouter` — connects N `IMavlinkConnection`s, forwards frames between them with configurable per-(srcSysId, msgid) filters. Re-signs at the boundary if signing policies differ.
- Tests: sign-and-verify roundtrip, replay-attack reject (timestamp regression), router 2-link forward with sysid filter, router preserves sequence per link.
- Docs: `docs/articles/signing.md`, `docs/articles/routing.md`.
- Update CLAUDE.md "Frame decode invariants" — signed frames are no longer dropped.

**Exit:** a `mavlink-router`-equivalent works in pure C#.

### M8 — Payload microservices: camera, gimbal v2, gripper, winch, logging closure

The long tail of "is it a full SDK." Each is independently shippable.

- **Camera Protocol v2:** allowlist `CAMERA_INFORMATION`, `CAMERA_SETTINGS`, `CAMERA_CAPTURE_STATUS`, `CAMERA_IMAGE_CAPTURED`, `VIDEO_STREAM_INFORMATION`. Map `MAV_CMD_IMAGE_START_CAPTURE` and `VIDEO_START_*` to typed methods. New `MavNet.Payload.Camera` namespace.
- **Gimbal v2:** allowlist `GIMBAL_MANAGER_INFORMATION`, `_STATUS`, `_SET_ATTITUDE`, `_SET_PITCHYAW`, `GIMBAL_DEVICE_*`. New `MavNet.Payload.Gimbal`.
- **Gripper / Winch:** `MAV_CMD_DO_GRIPPER`, `MAV_CMD_DO_WINCH` typed wrappers on `Drone`.
- **Dataflash log integration:** `LOG_REQUEST_LIST` / `LOG_ENTRY` / `LOG_REQUEST_DATA` / `LOG_DATA` for autopilots that don't expose logs via FTP. Hybrid downloader picks the right protocol per stack.
- **Component-aware addressing:** today `MavId` is sysid+compid but `Vehicle` collapses components. Add a `Components` registry per vehicle, expose per-component subscriptions.
- Tests: one [Fact] per camera/gimbal/winch protocol path through `FakeMavlinkConnection`; dataflash log download roundtrip.
- Docs: `docs/articles/payloads.md`, `docs/articles/components.md`.

**Exit:** "everything" v1.0 scope met. Ship NuGet 1.0.

---

## Cross-cutting workstreams (every milestone)

- **Allowlist hygiene:** every new message follows the CLAUDE.md "Code generation" 4-step ritual (regen, dispatcher event + switch arm, `IMavlinkConnection` event, roundtrip test + dispatch test).
- **Threading-friendly API for GCS:** events fire on the receive thread (CLAUDE.md "Threading model"); every new event-producing layer ships with an `IAsyncEnumerable<T>` adapter or `Channel<T>` helper so Blazor/WPF consumers do not have to marshal manually. Lives in `MavNet.Core` as `EventStream<T>`.
- **Sample apps:** one new sample per ~2 milestones, in `examples/` — `MavNet.Probe` (have), `MavNet.MissionCli`, `MavNet.ParamDumper`, `MavNet.LogDownloader`, `MavNet.MiniGcs` (Blazor).
- **Docs ship in the same PR as code and tests.** Update `CLAUDE.md` "Architecture" when a new top-level project lands.

---

## Critical files this roadmap touches

- `src/MavNet.Transport.Udp/MavlinkConnection.cs` — dispatcher grows ~30 events; base extracted in M3.
- `tools/MavNet.CodeGen/` — multi-dialect emission in M4; include resolution.
- `tools/MavNet.CodeGen/allowlist.txt` — grows every milestone; split per dialect in M4.
- `src/MavNet.PX4/Base/Vehicle.cs` — gains `Parameters`, `Telemetry`, `LinkHealth`, `Components` properties.
- `src/MavNet.Protocol/MavlinkFrame.cs` — signed-frame parsing in M7.
- `tests/MavNet.Protocol.Generated.Tests/MessageRoundtripTests.cs` — one new `[Fact]` per allowlisted message.

## Reusable patterns to lean on

- `PendingCommand` / `CommandOutcome` in `Vehicle.cs` — copy this pattern for `PARAM_SET` confirmation (M2), FTP opcode-reply correlation (M6), `MISSION_*` retries (M1, already used).
- `FakeMavlinkConnection` in `tests/MavNet.PX4.Tests/` — every new protocol gets a fake-driven test; no SITL required in CI.
- `IStateObservable<TState>` + `StateRate` — already powers `Drone.SubscribeState`; reuse for `LinkHealth`, parameter-change notifications, camera-capture-status streams.

---

## Verification (per milestone)

1. `dotnet build MavNet.slnx` clean (warnings = errors).
2. `dotnet test MavNet.slnx` green on Linux + Windows CI.
3. Codegen drift check still passes.
4. Manual SITL smoke against the matching autopilot: PX4 SITL via `make px4_sitl jmavsim` for M1–M3 and M5–M8; ArduPilot SITL via `sim_vehicle.py` for M4.
5. New `docs/articles/<topic>.md` builds in DocFX with no warnings.
6. NuGet package published as `1.0.0-alpha.<milestone>` so each milestone is consumable.

**End-state v1.0:** all 8 milestones merged, NuGet 1.0.0 published, sample `MavNet.MiniGcs` Blazor app demonstrates the SDK driving both PX4 and ArduPilot SITL side-by-side.
