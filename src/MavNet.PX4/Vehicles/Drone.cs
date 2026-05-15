using MavNet.Core;
using MavNet.PX4.Base;
using MavNet.PX4.Missions;
using MavNet.Transport.Udp;
using Microsoft.Extensions.Logging;
using MavNet.Protocol.Generated.Enums;
using MavNet.Protocol.Generated.Messages;

namespace MavNet.PX4.Vehicles;

/// <summary>
/// Immutable per-fire snapshot of a <see cref="Drone"/>'s observable state. Captured
/// atomically when <see cref="Drone.SubscribeState(Action{DroneState}, StateRate)"/>
/// delivers, so consumers see a coherent view (no torn reads across separate property
/// gets). Use this overload from telemetry recorders, replay, and anywhere a handler
/// might run on a different thread from the receive loop.
/// </summary>
public readonly record struct DroneState(
    double Lat,
    double Lon,
    double Alt,
    double Hdg,
    double Vel,
    string Mode,
    bool Armed,
    double Battery,
    int Sats,
    bool LinkUp)
{
    /// <summary>Current mission item seq from MISSION_CURRENT, or <c>-1</c> if unknown.</summary>
    public int MissionCurrentSeq { get; init; } = -1;

    /// <summary>Total mission item count from MISSION_CURRENT, or <c>-1</c> if unknown.</summary>
    public int MissionTotal { get; init; } = -1;

    /// <summary>Cumulative count of MISSION_ITEM_REACHED events seen.</summary>
    public int MissionReachedCount { get; init; }

    /// <summary>Mission-execution state (Active / Paused / Complete / …).</summary>
    public MissionState MissionState { get; init; }

    /// <summary>On-vehicle waypoint-plan opaque id (0 = no plan / no change-tracking).</summary>
    public uint MissionOpaqueId { get; init; }

    /// <summary>On-vehicle geofence-plan opaque id (0 = no plan / no change-tracking).</summary>
    public uint FenceOpaqueId { get; init; }

    /// <summary>On-vehicle rally-point-plan opaque id (0 = no plan / no change-tracking).</summary>
    public uint RallyOpaqueId { get; init; }
}

/// <summary>
/// A multirotor vehicle. Currently a thin marker over <see cref="Vehicle"/> so
/// consumers can pattern-match on type (<c>if (v is Drone)</c>) and so we have
/// a clear extension point for multirotor-specific behaviour later. The mission
/// protocol surface (upload / download / clear / fence / rally) lives on
/// <see cref="Vehicle"/> since boats, rovers, and planes use the same protocol.
///
/// <para>The static <see cref="ConnectAsync"/> factory is the one-liner entry
/// point for the common case: parse URI → open <see cref="MavlinkConnection"/>
/// → wait for first heartbeat → return a Drone that owns the connection.</para>
/// </summary>
public sealed class Drone : Vehicle, IStateObservable<DroneState>
{
    /// <summary>Capture an immutable snapshot of the current observable state.</summary>
    public DroneState Snapshot() => new(
        Lat, Lon, Alt, Hdg, Vel, Mode, Armed, Battery, Sats, LinkUp)
    {
        MissionCurrentSeq    = MissionCurrentSeq,
        MissionTotal         = MissionTotal,
        MissionReachedCount  = MissionReachedCount,
        MissionState         = MissionState,
        MissionOpaqueId      = MissionOpaqueId,
        FenceOpaqueId        = FenceOpaqueId,
        RallyOpaqueId        = RallyOpaqueId,
    };

    /// <inheritdoc cref="IStateObservable{TState}.SubscribeState(Action{TState}, StateRate)"/>
    public StateSubscription SubscribeState(Action<DroneState> handler, StateRate rate)
    {
        ArgumentNullException.ThrowIfNull(handler);
        // Reuses the base throttler. One snapshot per fire — cheap (~10 doubles).
        return SubscribeState(() => handler(Snapshot()), rate);
    }

    private Drone(MavlinkConnection conn, byte sys, byte comp, string? cs,
        ILogger<Vehicle>? logger)
        : base(conn, sys, comp, ownsConnection: true, heartbeatTimeout: null, logger: logger)
    {
        ConnectionString = cs;
    }

    /// <summary>
    /// One-liner factory for the common case. Opens a UDP <see cref="MavlinkConnection"/>,
    /// waits for the first inbound heartbeat, returns a <see cref="Drone"/> that owns the
    /// connection. Disposing the Drone closes the socket.
    /// </summary>
    /// <param name="connectionString">URI of the form
    ///   <c>udp://localBind:localPort?rhost=remote&amp;rport=remotePort</c>.
    ///   See <see cref="ConnectionString"/>.</param>
    /// <param name="timeout">How long to wait for the first heartbeat before throwing.</param>
    /// <param name="connectionLogger">Optional logger for the underlying transport.</param>
    /// <param name="vehicleLogger">Optional logger for the Vehicle layer.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="TimeoutException">No heartbeat arrived within <paramref name="timeout"/>.</exception>
    public static async Task<Drone> ConnectAsync(
        string connectionString,
        TimeSpan timeout,
        ILogger<MavlinkConnection>? connectionLogger = null,
        ILogger<Vehicle>? vehicleLogger = null,
        CancellationToken ct = default)
    {
        var (local, remote) = Transport.Udp.ConnectionString.Parse(connectionString);
        var conn = new MavlinkConnection(local, remote, logger: connectionLogger);

        // Capture the first heartbeat to learn (sys, comp), then construct the Drone.
        var firstHb = new TaskCompletionSource<(MavId sender, Heartbeat hb)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(MavId s, Heartbeat hb, DateTime _) => firstHb.TrySetResult((s, hb));
        conn.HeartbeatReceived += Handler;

        conn.Start();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            using var reg = cts.Token.Register(() => firstHb.TrySetCanceled(cts.Token));

            (MavId sender, Heartbeat hb) first;
            try { first = await firstHb.Task.ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"No vehicle heartbeat received within {timeout.TotalSeconds:F0} s on '{connectionString}'.");
            }

            // Stop the discovery handler — the Drone will re-subscribe via its base ctor.
            conn.HeartbeatReceived -= Handler;

            var drone = new Drone(conn, first.sender.SystemId, first.sender.ComponentId, connectionString, vehicleLogger);
            // The Drone's own HeartbeatReceived subscription was wired up by the base ctor;
            // the heartbeat we already received above sets state on the next one to arrive.
            return drone;
        }
        catch
        {
            conn.HeartbeatReceived -= Handler;
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
