# MavNet

MavNet is a small .NET MAVLink v2 stack for UDP vehicle links. It gives you typed MAVLink messages, a UDP transport, and a PX4-oriented `Drone` facade for common telemetry and commands.

> [!WARNING]
> MavNet can send real arm, takeoff, land, and RTL commands. Start with PX4 SITL, keep props off real vehicles, and treat every connection string as capable of moving hardware.

## Status

- Target framework: `net10.0`
- Transport today: UDP
- Dialect today: `common.xml` (PX4-oriented)
- High-level vehicle facade today: PX4 multirotor (`Drone`)
- Package status: not published to NuGet yet
- Tests: xUnit + FluentAssertions across all layers, CI on Ubuntu + Windows

Where we stand: roughly **20–25%** of a full general-purpose C# MAVLink SDK. The wire-format core, codegen pipeline, UDP transport, send path, and command-ACK correlation are solid. The protocol surface above that is still thin.

## Roadmap

A condensed view. Full version in [ROADMAP.md](ROADMAP.md).

**Done**

- MAVLink v2 frame decode / CRC / truncation / forward-compat
- Codegen from `common.xml` (single dialect)
- UDP transport + heartbeat + typed dispatcher events
- Zero-alloc static-abstract send path
- PX4 `Drone` facade: arm, disarm, takeoff, land, RTL with COMMAND_ACK correlation
- Mission protocol: upload / download / clear / start state machines for waypoints, geofence and rally points; live `MISSION_CURRENT` / `MISSION_ITEM_REACHED` on `Vehicle`
- Rate-controlled state subscription
- Test harness, CI matrix, codegen-drift check, SourceLink, deterministic builds
- Static analysis: .NET analyzers (`Recommended` + code-style in build, generated code excluded), allowlist→`IMavlinkConnection` wiring consistency test

**Next, in order**

| Milestone | Scope |
|---|---|
| M2 | Parameter Protocol: PARAM_REQUEST_LIST / VALUE / SET with cache and ACK correlation |
| M3 | Transports: TCP, serial; URI-driven `MavlinkConnection.Open(...)` |
| M4 | Dialect-agnostic core + ArduPilot (`ardupilotmega.xml`, Copter/Plane/Rover facades) |
| M5 | Telemetry rate control (`SET_MESSAGE_INTERVAL`) + `LinkHealth` (rtt, packet loss) |
| M6 | FTP (msgid 110) + dataflash log download + TLOG write/read |
| M7 | MAVLink v2 signing (HMAC-SHA256) + multi-link routing / bridging |
| M8 | Camera v2, Gimbal v2, gripper, winch, component-aware addressing |

Each milestone is a thin vertical slice — code + tests + docs, NuGet-shippable. v1.0 lands after M8.

## Quick Start

Add project references while the package is unpublished:

```xml
<ItemGroup>
  <ProjectReference Include="path/to/MavNet/src/MavNet.PX4/MavNet.PX4.csproj" />
</ItemGroup>
```

Connect to PX4 SITL:

```csharp
using MavNet.Core;
using MavNet.PX4;
using MavNet.PX4.Vehicles;

await using Drone drone = await Drone.ConnectAsync(
    "udp://0.0.0.0:14550?rhost=127.0.0.1&rport=18570",
    TimeSpan.FromSeconds(30));

using StateSubscription sub = drone.SubscribeState(
    s => Console.WriteLine($"{s.Mode} armed={s.Armed} alt={s.Alt:F1}m bat={s.Battery:F0}%"),
    StateRate.Hz(2));

CommandOutcome outcome = await drone.ArmAsync();
Console.WriteLine(outcome.Result);
```

Run the bundled probe:

```bash
dotnet run -c Release --project examples/MavNet.Probe -- "udp://0.0.0.0:14550?rhost=127.0.0.1&rport=18570"
```

Probe keys: `A` arm, `D` disarm, `T` takeoff to 10 m, `L` land, `R` return to launch, `M` mission demo (upload + start a square around the current position), `Q` quit.

## Connection Strings

`udp://localHost:localPort?rhost=remoteHost&rport=remotePort`

Example for local PX4 SITL:

```text
udp://0.0.0.0:14550?rhost=127.0.0.1&rport=18570
```

Defaults are `localHost=0.0.0.0`, `localPort=14550`, `rhost=127.0.0.1`, and `rport=18570`.

## Build

```bash
dotnet build MavNet.slnx
```

Warnings are errors. Generated protocol files are committed, so a normal build does not require regeneration.

## Generated Protocol Code

Generated files live under:

- `src/MavNet.Protocol.Generated/`
- `src/MavNet.Protocol/Generated/MessageRegistry.cs`

Do not hand-edit them. Regenerate with:

```bash
dotnet run --project tools/MavNet.CodeGen
```

When adding a message to `tools/MavNet.CodeGen/allowlist.txt`, regenerate, then add its typed event and dispatch case in `src/MavNet.Transport.Udp/MavlinkConnection.cs`. Surface it through `Vehicle` or `Drone` only when it belongs in the high-level API. `AllowlistWiringConsistencyTests` fails the build if the `IMavlinkConnection` event for an inbound message is forgotten (send-only messages are listed explicitly).

## Docs

The full documentation site is published to GitHub Pages on every push to `main`.

Conceptual articles live in `docs/articles/` as plain Markdown. API reference is generated automatically from `///` XML comments in source.

**Edit and preview locally:**

```bash
dotnet docfx docfx.json --serve --watch
```

Open [http://localhost:8080](http://localhost:8080). The site rebuilds automatically when you save a Markdown file.

**Build without serving:**

```bash
dotnet docfx docfx.json
```

Output goes to `_site/` (git-ignored).

Articles:
- [Getting started](docs/articles/getting-started.md)
- [Architecture](docs/articles/architecture.md)
- [Missions](docs/articles/missions.md)
