# MavNet

MavNet is a small .NET MAVLink v2 stack for UDP vehicle links. It gives you typed MAVLink messages, a UDP transport, and a PX4-oriented `Drone` facade for common telemetry and commands.

> [!WARNING]
> MavNet can send real arm, takeoff, land, and RTL commands. Start with PX4 SITL, keep props off real vehicles, and treat every connection string as capable of moving hardware.

## Status

- Target framework: `net10.0`
- Transport today: UDP
- High-level vehicle facade today: PX4 multirotor (`Drone`)
- Package status: not published to NuGet yet
- Tests: no test project yet

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

Probe keys: `A` arm, `D` disarm, `T` takeoff to 10 m, `L` land, `R` return to launch, `Q` quit.

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

When adding a message to `tools/MavNet.CodeGen/allowlist.txt`, regenerate, then add its typed event and dispatch case in `src/MavNet.Transport.Udp/MavlinkConnection.cs`. Surface it through `Vehicle` or `Drone` only when it belongs in the high-level API.

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
