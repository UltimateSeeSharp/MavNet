# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build / Run

- Solution file is `MavNet.slnx` (new XML solution format — use `dotnet` CLI, not legacy `.sln` tools).
- Target framework: `net10.0`, nullable enabled, `TreatWarningsAsErrors=true` (set in `Directory.Build.props`). A warning will fail the build.
- Build everything: `dotnet build MavNet.slnx`
- Run the sample client against PX4 SITL: `dotnet run -c Release --project examples/MavNet.Probe -- "udp://0.0.0.0:14550?rhost=127.0.0.1&rport=18570"`
- Run all tests: `dotnet test MavNet.slnx`. Test projects live under `tests/`, one per `src/` project, using xUnit + FluentAssertions. CI (`.github/workflows/ci.yml`) runs build + test on Ubuntu and Windows, plus a codegen-drift check on Linux.

## Code generation (critical workflow)

Most of `src/MavNet.Protocol.Generated/` and `src/MavNet.Protocol/Generated/MessageRegistry.cs` are **emitted** — do not hand-edit them. They will be wiped on every regen.

To regenerate:

```
dotnet run --project tools/MavNet.CodeGen
```

With no args it uses repo-relative defaults: spec `specs/common.xml`, allowlist `tools/MavNet.CodeGen/allowlist.txt`, output into the two `Generated` locations above. Override with `--spec`, `--allowlist`, `--generated-out`, `--registry-out`.

The allowlist controls which messages get emitted as record structs. Enums are emitted for everything. **When you add a message to `allowlist.txt`:**

1. Regenerate (above).
2. Add a new `event Action<MavId, NewMsg, DateTime>?` and a `case NewMsg.MsgId:` arm in `src/MavNet.Transport.Udp/MavlinkConnection.cs` — the dispatcher silently drops msgids it doesn't know. Also add the event to `IMavlinkConnection` so test fakes stay in sync.
3. If the message is something a `Drone`/vehicle should surface, wire it through `src/MavNet.PX4/Base/Vehicle.cs` and `Vehicles/Drone.cs`.
4. Add a roundtrip `[Fact]` for the new message in `tests/MavNet.Protocol.Generated.Tests/MessageRoundtripTests.cs` and a dispatch test in `tests/MavNet.Transport.Udp.Tests/MavlinkConnectionDispatchTests.cs`.

`MessageRegistry` lives in the `MavNet.Protocol` assembly (not `.Generated`) so that `MavlinkFrame.TryDecode` can reach it without `Protocol` referencing `Protocol.Generated` (which would be circular). Keep it that way.

## Architecture

Layering, bottom up:

- **MavNet.Core** — `MavId` (sysid+compid pair). No deps.
- **MavNet.Protocol** — wire-format primitives: `MavlinkFrame` (ref struct decoder of MAVLink v2 frames, `0xFD` magic), `Crc16` (X.25), `IMavlinkMessage<TSelf>` contract (CRTP, static abstracts for `MsgId`/`CrcExtra`/`Min`/`MaxPayloadLength`/`Encode`/`Decode`). Holds the generated `MessageRegistry` (msgid → CrcExtra + payload bounds) needed for frame validation.
- **MavNet.Protocol.Generated** — emitted `readonly record struct` per allowlisted message, plus all enums and `MAV_CMD_*` command structs. Implements `IMavlinkMessage<TSelf>`.
- **MavNet.Transport.Udp** — `MavlinkConnection` owns one UDP socket, one receive loop, and a fixed-rate GCS heartbeat timer. Decodes frames and fires typed `event Action<MavId, T, DateTime>` per known msgid. `ConnectionString` parses `udp://host:port?rhost=...&rport=...` URIs.
- **MavNet.PX4** — higher-level vehicle façade. `Vehicle` (base) holds the connection, sysid tracking, `COMMAND_LONG`→`COMMAND_ACK` correlation via `PendingCommand`/`CommandOutcome`, and the mission-protocol surface (upload/download/clear × waypoints/fence/rally + `StartMissionAsync` / `SetCurrentMissionItemAsync`). Mission state machines live in `MavNet.PX4.Missions.MissionClient` (one per `MAV_MISSION_TYPE`, lazily built by Vehicle). `Drone` adds arm/disarm/takeoff/land/RTL and the typed `DroneState` snapshot.
- **MavNet.Probe** — console example.

`Vehicle` depends on `IMavlinkConnection` (interface in `MavNet.Transport.Udp`), not the concrete `MavlinkConnection`. Tests substitute a fake (`FakeMavlinkConnection` in `tests/MavNet.PX4.Tests/`). The command timeout in `Vehicle.ExecuteCommandAsync` is injectable (`commandTimeout` ctor param, defaults to 6 s) so race-case tests stay sub-second.

### Frame decode invariants (`MavlinkFrame.TryDecode`)

Drop the frame (return false) on: v1 magic, signed frames (`incompat & 0x01`), any unknown incompat bit (MAVLink spec requires this for forward compat), unknown msgid (no CrcExtra → can't verify), payload length outside `[MinPayloadLength, MaxPayloadLength]`, or CRC mismatch. The unknown-incompat-bit drop is deliberate — relaxing it can corrupt forwarded frames once new spec bits ship.

### Send path

`MavlinkConnection.Send<T>(T msg)` resolves `MsgId`/`CrcExtra`/`MaxPayloadLength` at the call site via static abstracts — no runtime dictionary lookup. Trailing zero bytes are stripped down to `MinPayloadLength` to perform MAVLink v2 wire truncation. Sequence is `Interlocked.Increment`.

### Threading model

`MavlinkConnection` events fire **synchronously on the receive thread**. UI consumers (Blazor, WPF) must marshal themselves. Subscriber exceptions are swallowed so a buggy handler can't kill the receive loop — preserve that.

## Conventions

- Generated files have a header comment marking them as such; never edit by hand, change the emitter in `tools/MavNet.CodeGen/Emitters/` instead.
- Casing rules for generated identifiers live in `tools/MavNet.CodeGen/Casing.cs`.
- Use `Span<byte>` / `stackalloc` on the hot encode/decode paths — don't allocate per-frame.
- **Ship docs and tests alongside any change.** Every new feature, behavior change, or bug fix lands with: (1) the code, (2) a test that exercises it under `tests/<matching project>.Tests/`, and (3) any doc updates it makes obsolete — XML doc comments on the changed API, the matching article under `docs/articles/`, and any section of this `CLAUDE.md` that became stale. No "I'll add tests later" — if it's worth merging, it's worth proving and documenting in the same PR. Bug fixes specifically must start with a failing test that reproduces the bug, then the fix.
- **Keep `README.md` and `ROADMAP.md` current too.** Whenever a change advances functionality, project scope, or status, update the project-status text in `README.md` (the **Done** / **Next** lists, milestone table, probe keybindings, articles list) and the matching row / milestone in `ROADMAP.md` (Current-state table, allowlist count, milestone definitions) in the same PR. Treat them as part of the deliverable, not optional polish. "Done = done on disk" — flip status as soon as the work lands, not when it ships to NuGet.

## Keeping this file current

Update this file when something it states becomes wrong or incomplete: a build/run command changes, a layer's responsibility shifts, the codegen workflow or allowlist→dispatcher wiring changes, frame-decode invariants are relaxed/tightened, the threading model changes, or a new top-level project is added. Don't append change logs — edit the affected section in place and delete anything no longer true.
