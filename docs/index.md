# MavNet

Typed MAVLink v2 over UDP for .NET, with a PX4 `Drone` facade for common telemetry and commands.

> [!WARNING]
> MavNet can send real vehicle commands. Use SITL first, keep props off real vehicles, and verify the UDP endpoint before sending arm or takeoff.

```csharp
using MavNet.Core;
using MavNet.PX4.Vehicles;

await using Drone drone = await Drone.ConnectAsync(
    "udp://0.0.0.0:14550?rhost=127.0.0.1&rport=18570",
    TimeSpan.FromSeconds(30));

using StateSubscription sub = drone.SubscribeState(
    s => Console.WriteLine($"{s.Mode} alt={s.Alt:F1}m bat={s.Battery:F0}%"),
    StateRate.Hz(2));
```

## Quick links

- [Getting Started](articles/getting-started.md)
- [Architecture](articles/architecture.md)
- [API Reference](../api/index.md)
