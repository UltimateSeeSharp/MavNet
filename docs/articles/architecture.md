# Architecture

MavNet is intentionally small. Each layer depends only on layers below it.

```text
MavNet.Core
MavNet.Protocol
MavNet.Protocol.Generated
MavNet.Transport.Udp
MavNet.PX4
MavNet.Probe
```

## Layers

- `MavNet.Core`: shared primitives such as `MavId`, `StateRate`, and `StateSubscription`.
- `MavNet.Protocol`: MAVLink v2 frame decode/encode primitives, X.25 CRC, message contracts, and the generated message registry.
- `MavNet.Protocol.Generated`: generated message structs, enums, and command structs.
- `MavNet.Transport.Udp`: one UDP socket, one receive loop, typed message events, and outbound GCS heartbeat.
- `MavNet.PX4`: vehicle-level state and command helpers. `Drone` is the current high-level entry point.
- `MavNet.Probe`: console sample for SITL/manual smoke testing.

## Frame Rules

`MavlinkFrame.TryDecode` accepts MAVLink v2 frames only. It drops frames with:

- MAVLink v1 magic
- signed frames
- unknown incompatibility bits
- unknown message ids
- payload lengths outside the generated min/max bounds
- CRC mismatch

> [!IMPORTANT]
> Unknown incompatibility bits are dropped on purpose. MAVLink requires this for forward compatibility; accepting them can make future frames unsafe to forward or decode.

## Generated Code

Do not hand-edit generated files:

- `src/MavNet.Protocol.Generated/`
- `src/MavNet.Protocol/Generated/MessageRegistry.cs`

Regenerate with:

```bash
dotnet run --project tools/MavNet.CodeGen
```

Adding a message means:

1. Add it to `tools/MavNet.CodeGen/allowlist.txt`.
2. Regenerate.
3. Add a typed event and dispatch case in `MavlinkConnection`.
4. Surface it in `Vehicle` / `Drone` only if it belongs in the high-level API.

## Runtime Model

- Receive events fire synchronously on the UDP receive thread.
- Subscriber exceptions are swallowed so one bad handler does not kill the receive loop.
- `Send<T>` uses generated static message metadata and strips MAVLink v2 trailing zero payload bytes.
- `Vehicle` correlates `COMMAND_LONG` with `COMMAND_ACK`; some commands also wait for a confirming heartbeat state change.

Keep event handlers short. UI apps should dispatch from handlers into their UI synchronization context.
