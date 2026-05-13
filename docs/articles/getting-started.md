# Getting Started

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- A MAVLink v2 autopilot or SITL simulator reachable over UDP

## Install

MavNet is not yet published on NuGet. Clone the repository and add project references directly:

```xml
<ProjectReference Include="path/to/MavNet/src/MavNet.PX4/MavNet.PX4.csproj" />
```

## Connect to a drone

Use `Drone.ConnectAsync` with a UDP connection string. The call blocks until the first heartbeat is received from the vehicle (or the timeout elapses):

```csharp
using MavNet.PX4.Vehicles;

Drone drone = await Drone.ConnectAsync(
    "udp://0.0.0.0:14550?rhost=127.0.0.1&rport=18570",
    TimeSpan.FromSeconds(30));

await using (drone)
{
    Console.WriteLine($"Connected to {drone.DeviceId} (type={drone.VehicleType})");
    // ...
}
```

The connection string format is `udp://lhost:lport?rhost=...&rport=...` where `lhost:lport` is the local bind address and `rhost:rport` is the remote autopilot.

## Subscribe to telemetry

Telemetry properties (`Armed`, `Mode`, `Lat`, `Lon`, `Alt`, `Battery`, …) are updated on the receive thread. Read them from any thread:

```csharp
drone.HeartbeatReceived += (hb, receivedAt) =>
{
    Console.WriteLine($"armed={drone.Armed} mode={drone.Mode} bat={drone.Battery:F0}%");
};
```

> **Threading note:** Events fire synchronously on the UDP receive thread. Marshal to the UI thread yourself if needed (e.g. `Dispatcher.InvokeAsync` in WPF).

## Send flight commands

```csharp
// Arm
CommandOutcome arm = await drone.ArmAsync(cancellationToken);

// Takeoff to 10 m
CommandOutcome takeoff = await drone.TakeoffAsync(10.0, cancellationToken);

// Land
CommandOutcome land = await drone.LandAsync(cancellationToken);

// Return to launch
CommandOutcome rtl = await drone.ReturnToLaunchAsync(cancellationToken);

// Disarm
CommandOutcome disarm = await drone.DisarmAsync(cancellationToken);
```

Each call sends `COMMAND_LONG` and waits for the matching `COMMAND_ACK`. The returned `CommandOutcome` contains the result code, ACK result (if any), and elapsed time.

## Run against PX4 SITL

```bash
# In one terminal — start PX4 SITL (adjust path as needed)
make px4_sitl gazebo

# In another terminal — run the bundled probe
dotnet run -c Release --project examples/MavNet.Probe -- "udp://0.0.0.0:14550?rhost=127.0.0.1&rport=18570"
```

Keys in the probe console: `A` Arm, `D` Disarm, `T` Takeoff (10 m), `L` Land, `R` RTL, `Q` Quit.
