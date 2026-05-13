# Getting Started

This is the shortest path from a PX4 SITL UDP endpoint to typed telemetry and commands.

> [!WARNING]
> `ArmAsync`, `TakeoffAsync`, `LandAsync`, and `ReturnToLaunchAsync` send real MAVLink commands. Do first runs in SITL.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- A MAVLink v2 vehicle or simulator reachable over UDP

## Install

MavNet is not on NuGet yet. Add a project reference:

```xml
<ItemGroup>
  <ProjectReference Include="path/to/MavNet/src/MavNet.PX4/MavNet.PX4.csproj" />
</ItemGroup>
```

## Connect

`Drone.ConnectAsync` opens UDP, waits for the first heartbeat, then returns a `Drone` that owns the connection.

```csharp
using MavNet.Core;
using MavNet.PX4.Vehicles;

await using Drone drone = await Drone.ConnectAsync(
    "udp://0.0.0.0:14550?rhost=127.0.0.1&rport=18570",
    TimeSpan.FromSeconds(30));

Console.WriteLine($"Connected to {drone.DeviceId} ({drone.VehicleType})");
```

Connection strings use:

```text
udp://localHost:localPort?rhost=remoteHost&rport=remotePort
```

For PX4 SITL, the common value is:

```text
udp://0.0.0.0:14550?rhost=127.0.0.1&rport=18570
```

## Read State

```csharp
using StateSubscription sub = drone.SubscribeState(
    s => Console.WriteLine($"{s.Mode} armed={s.Armed} alt={s.Alt:F1}m sats={s.Sats}"),
    StateRate.Hz(2));
```

`SubscribeState` can deliver immutable `DroneState` snapshots. Use `StateRate.Raw` for every state-affecting packet, or throttle with `StateRate.Hz(...)` / `StateRate.Every(...)`.

> [!NOTE]
> Transport events and raw state changes fire on the receive thread. UI apps should marshal back to their UI thread.

## Send Commands

```csharp
CommandOutcome arm = await drone.ArmAsync(cancellationToken);
CommandOutcome takeoff = await drone.TakeoffAsync(10.0, cancellationToken);
CommandOutcome land = await drone.LandAsync(cancellationToken);
CommandOutcome rtl = await drone.ReturnToLaunchAsync(cancellationToken);
CommandOutcome disarm = await drone.DisarmAsync(cancellationToken);
```

Command helpers send `COMMAND_LONG` and wait for the matching `COMMAND_ACK` plus, where possible, a confirming state change.

## Probe App

```bash
dotnet run -c Release --project examples/MavNet.Probe -- "udp://0.0.0.0:14550?rhost=127.0.0.1&rport=18570"
```

Keys in the probe console: `A` Arm, `D` Disarm, `T` Takeoff (10 m), `L` Land, `R` RTL, `Q` Quit.
