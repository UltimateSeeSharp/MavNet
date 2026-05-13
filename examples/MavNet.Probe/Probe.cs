using MavNet.PX4;
using MavNet.PX4.Vehicles;

namespace MavNet.Probe;

/// <summary>
/// Minimal console client for MavNet — exercises the one-liner factory,
/// telemetry events, and flight commands against PX4 SITL.
///
/// Usage:
///   dotnet run -c Release --project MavNet.Probe
///   dotnet run -c Release --project MavNet.Probe -- "udp://0.0.0.0:14550?rhost=127.0.0.1&amp;rport=18570"
///
/// Keys (case-insensitive):
///   A  Arm        D  Disarm       T  Takeoff (10 m)
///   L  Land       R  RTL          Q  Quit
/// </summary>
internal sealed class Probe
{
    private const string DefaultUri = "udp://0.0.0.0:14550?rhost=127.0.0.1&rport=18570";

    private readonly Drone _drone;
    private readonly CancellationTokenSource _cts = new();
    private long _heartbeatCount;
    private bool? _previousArmed;
    private readonly object _armedLock = new();

    private Probe(Drone drone)
    {
        _drone = drone;
        _drone.HeartbeatReceived += OnHeartbeat;
    }

    public static async Task<int> Main(string[] args)
    {
        var uri = args.Length >= 1 ? args[0] : DefaultUri;

        Log($"connecting via {uri} ...");
        Drone drone;
        try
        {
            drone = await Drone.ConnectAsync(uri, TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException ex)
        {
            Log(ex.Message);
            return 1;
        }

        await using (drone)
        {
            var probe = new Probe(drone);
            Log($"connected to {drone.DeviceId} (type={drone.VehicleType})");
            Console.WriteLine();
            Console.WriteLine("Keys: A=Arm  D=Disarm  T=Takeoff(10m)  L=Land  R=RTL  Q=Quit");
            Console.WriteLine();
            return await probe.RunAsync();
        }
    }

    private async Task<int> RunAsync()
    {
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; _cts.Cancel(); };
        var snapshotLoop = Task.Run(SnapshotLoopAsync);

        while (!_cts.IsCancellationRequested)
        {
            if (!Console.KeyAvailable)
            {
                await Task.Delay(50);
                continue;
            }
            var key = Console.ReadKey(intercept: true);
            await HandleKeyAsync(char.ToUpperInvariant(key.KeyChar));
        }

        try { await snapshotLoop.ConfigureAwait(false); } catch { }
        return 0;
    }

    private async Task HandleKeyAsync(char key)
    {
        var now = DateTime.Now;
        switch (key)
        {
            case 'A':
                Log(">> Arm", now);
                PrintOutcome("Arm", await _drone.ArmAsync(_cts.Token));
                break;
            case 'D':
                Log(">> Disarm", now);
                PrintOutcome("Disarm", await _drone.DisarmAsync(_cts.Token));
                break;
            case 'T':
                Log(">> Takeoff 10m", now);
                PrintOutcome("Takeoff", await _drone.TakeoffAsync(10.0, _cts.Token));
                break;
            case 'L':
                Log(">> Land", now);
                PrintOutcome("Land", await _drone.LandAsync(_cts.Token));
                break;
            case 'R':
                Log(">> RTL", now);
                PrintOutcome("RTL", await _drone.ReturnToLaunchAsync(_cts.Token));
                break;
            case 'Q':
                Log("Quitting ...");
                _cts.Cancel();
                break;
        }
    }

    private async Task SnapshotLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await Task.Delay(1000, _cts.Token); }
            catch (OperationCanceledException) { return; }

            Console.WriteLine(
                $"[{DateTime.Now:HH:mm:ss.fff}] HBs={Interlocked.Read(ref _heartbeatCount)} " +
                $"armed={_drone.Armed} mode={_drone.Mode,-12} " +
                $"lat={_drone.Lat:F6} lon={_drone.Lon:F6} " +
                $"alt={_drone.Alt,5:F1}m vel={_drone.Vel,5:F2}m/s hdg={_drone.Hdg,5:F1}° " +
                $"sats={_drone.Sats} gps={_drone.GpsFix} bat={_drone.Battery:F0}% landed={_drone.LandedState}");
        }
    }

    private void OnHeartbeat(MavNet.Protocol.Generated.Messages.Heartbeat hb, DateTime receivedAt)
    {
        Interlocked.Increment(ref _heartbeatCount);
        bool changed;
        lock (_armedLock)
        {
            changed = _previousArmed.HasValue && _previousArmed.Value != _drone.Armed;
            _previousArmed = _drone.Armed;
        }
        if (changed)
            Console.WriteLine(
                $"[{receivedAt.ToLocalTime():HH:mm:ss.fff}] ★ ARMED CHANGED -> {_drone.Armed} (mode={_drone.Mode})");
    }

    private static void PrintOutcome(string verb, CommandOutcome o)
    {
        var ack = o.AckResult is null ? "" : $" ACK={o.AckResult}";
        Console.WriteLine(
            $"[{DateTime.Now:HH:mm:ss.fff}] << {verb}: {o.Result}{ack} (elapsed={o.Elapsed.TotalMilliseconds:F0}ms)");
    }

    private static void Log(string message, DateTime? at = null)
        => Console.WriteLine($"[{(at ?? DateTime.Now):HH:mm:ss.fff}] {message}");
}
