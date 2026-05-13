# Architecture

MavNet is layered so that each assembly only depends on layers below it. From bottom to top:

```
MavNet.Core
  └─ MavNet.Protocol
       ├─ MavNet.Protocol.Generated
       └─ MavNet.Transport.Udp
            └─ MavNet.PX4
```

---

## MavNet.Core

Defines `MavId` — a `(sysid, compid)` pair that identifies a MAVLink node. No dependencies.

---

## MavNet.Protocol

Wire-format primitives:

- **`MavlinkFrame`** — a `ref struct` that decodes MAVLink v2 frames in-place over a `ReadOnlySpan<byte>`. Magic byte is `0xFD`.
- **`Crc16`** — X.25 CRC implementation used for frame validation.
- **`IMavlinkMessage<TSelf>`** — CRTP contract with static abstracts: `MsgId`, `CrcExtra`, `MinPayloadLength`, `MaxPayloadLength`, `Encode`, `Decode`.
- **`MessageRegistry`** — maps msgid → (CrcExtra, min/max payload length). Kept here (not in `Protocol.Generated`) so `MavlinkFrame.TryDecode` can validate frames without a circular reference.

### Frame decode invariants

`MavlinkFrame.TryDecode` drops a frame (returns `false`) on any of:

| Condition | Reason |
|---|---|
| v1 magic (`0xFE`) | v1 not supported |
| Signed frame (`incompat_flags & 0x01`) | Signing not implemented |
| Unknown incompatibility bit | MAVLink spec requires drop for forward compatibility |
| Unknown msgid | No CrcExtra available → cannot verify CRC |
| Payload outside `[Min, Max]` | Malformed frame |
| CRC mismatch | Corrupt or spoofed frame |

The unknown-incompat-bit drop is intentional — relaxing it can corrupt forwarded frames once new spec bits ship.

---

## MavNet.Protocol.Generated

Auto-generated `readonly record struct` types for each allowlisted MAVLink message, plus all enums and `MAV_CMD_*` command structs. Each type implements `IMavlinkMessage<TSelf>`.

**Do not edit these files.** They are wiped on every codegen run:

```bash
dotnet run --project tools/MavNet.CodeGen
```

The allowlist (`tools/MavNet.CodeGen/allowlist.txt`) controls which messages get emitted as structs. After adding a message to the allowlist:

1. Regenerate.
2. Add an `event Action<MavId, NewMsg, DateTime>?` and a `case NewMsg.MsgId:` dispatch arm in `MavlinkConnection.cs`.
3. Wire telemetry through `Vehicle.cs` / `Drone.cs` if needed.

---

## MavNet.Transport.Udp

`MavlinkConnection` owns one UDP socket, one receive loop, and a fixed-rate GCS heartbeat timer.

### Receive path

Frames are decoded and dispatched as typed events:

```csharp
event Action<MavId, Heartbeat, DateTime>? HeartbeatReceived;
event Action<MavId, GlobalPositionInt, DateTime>? GlobalPositionIntReceived;
// …one per allowlisted message
```

Unrecognised msgids are silently dropped. Subscriber exceptions are swallowed so a buggy handler cannot kill the receive loop.

### Send path

```csharp
void Send<T>(T msg) where T : IMavlinkMessage<T>
```

Resolves `MsgId`, `CrcExtra`, and `MaxPayloadLength` via static abstracts at the call site — no runtime dictionary lookup. Trailing zero bytes are stripped to `MinPayloadLength` (MAVLink v2 payload truncation). Sequence number is `Interlocked.Increment`.

### Threading model

Events fire **synchronously on the receive thread**. UI consumers (Blazor, WPF) must marshal to their own thread. Do not block the event handler.

---

## MavNet.PX4

Higher-level vehicle façade built on top of `MavlinkConnection`.

- **`Vehicle`** (base) — tracks the active sysid, subscribes to transport events, and implements `COMMAND_LONG` → `COMMAND_ACK` correlation via `PendingCommand`/`CommandOutcome`.
- **`Drone`** — extends `Vehicle` with arm, disarm, takeoff, land, and RTL convenience methods.

Each command method sends `COMMAND_LONG` and asynchronously awaits the `COMMAND_ACK` reply, returning a `CommandOutcome` with the result, ACK code, and elapsed time.
