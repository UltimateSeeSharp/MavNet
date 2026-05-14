using MavNet.Protocol.Generated.Enums;
using MavNet.Protocol.Generated.Messages;

namespace MavNet.PX4.Missions;

/// <summary>
/// Protocol-shaped mission item — mirrors the MAVLink <c>MISSION_ITEM_INT</c> wire
/// layout but without the per-transaction fields (<c>Seq</c>, <c>TargetSystem</c>,
/// <c>TargetComponent</c>, <c>MissionType</c>). <see cref="MissionClient"/> stamps
/// those in when it sends each item.
///
/// <para>Coordinate scaling matches the spec: for global frames, <see cref="X"/> /
/// <see cref="Y"/> are <c>degrees × 1e7</c>; for local frames, <c>metres × 1e4</c>.
/// <see cref="Z"/> is always a float. Use the static factories
/// (<c>Waypoint</c>, <c>Takeoff</c>, <c>Land</c>, <c>ReturnToLaunch</c>) to avoid hand-scaling.</para>
///
/// <para>The same record is used for waypoint, geofence, and rally-point missions
/// — the <c>Command</c> field plus <c>MAV_MISSION_TYPE</c> (passed to the
/// <see cref="MissionClient"/>) determine the semantics.</para>
/// </summary>
public readonly record struct MissionItem(
    MavFrame Frame,
    MavCmd Command,
    float Param1,
    float Param2,
    float Param3,
    float Param4,
    int X,
    int Y,
    float Z,
    byte AutoContinue = 1)
{
    /// <summary>Build a NAV_WAYPOINT item from human-friendly degrees/metres. <paramref name="hold"/>
    /// is the time-to-hold in seconds at the waypoint (param1); <paramref name="acceptanceRadius"/>
    /// is the radius (m) within which the waypoint is considered reached (param2).</summary>
    public static MissionItem Waypoint(
        double latDeg, double lonDeg, float altMeters,
        float hold = 0f, float acceptanceRadius = 0f,
        MavFrame frame = MavFrame.GlobalRelativeAltInt) =>
        new(
            Frame: frame,
            Command: MavCmd.NavWaypoint,
            Param1: hold,
            Param2: acceptanceRadius,
            Param3: 0f,        // pass-through radius (0 = pass through)
            Param4: float.NaN, // desired yaw at waypoint (NaN = unchanged)
            X: ToInt1e7(latDeg),
            Y: ToInt1e7(lonDeg),
            Z: altMeters);

    /// <summary>Build a NAV_LAND item at the given coordinate. <paramref name="abortAlt"/>
    /// is the minimum altitude (m) the autopilot may abort a landing at (param1).</summary>
    public static MissionItem Land(
        double latDeg, double lonDeg,
        float abortAlt = 0f,
        MavFrame frame = MavFrame.GlobalRelativeAltInt) =>
        new(
            Frame: frame,
            Command: MavCmd.NavLand,
            Param1: abortAlt,
            Param2: 0f,
            Param3: 0f,
            Param4: float.NaN,
            X: ToInt1e7(latDeg),
            Y: ToInt1e7(lonDeg),
            Z: 0f);

    /// <summary>Build a NAV_TAKEOFF item at the given altitude.</summary>
    public static MissionItem Takeoff(
        float altMeters,
        float pitch = 0f,
        MavFrame frame = MavFrame.GlobalRelativeAltInt) =>
        new(
            Frame: frame,
            Command: MavCmd.NavTakeoff,
            Param1: pitch,
            Param2: 0f,
            Param3: 0f,
            Param4: float.NaN,
            X: 0,
            Y: 0,
            Z: altMeters);

    /// <summary>Build a NAV_RETURN_TO_LAUNCH item.</summary>
    public static MissionItem ReturnToLaunch() =>
        new(
            Frame: MavFrame.Mission,
            Command: MavCmd.NavReturnToLaunch,
            Param1: 0f, Param2: 0f, Param3: 0f, Param4: 0f,
            X: 0, Y: 0, Z: 0f);

    /// <summary>Encode degrees to MAVLink int32 (× 1e7), rounding half-away-from-zero.</summary>
    public static int ToInt1e7(double degrees) =>
        (int)Math.Round(degrees * 1e7, MidpointRounding.AwayFromZero);

    /// <summary>Decode MAVLink int32 (× 1e7) back to degrees.</summary>
    public static double FromInt1e7(int scaled) => scaled / 1e7;

    /// <summary>Wrap this item with the per-transaction stamping (sequence number, target,
    /// mission type) into a wire-shaped <see cref="MissionItemInt"/>. The <c>Current</c>
    /// field is always emitted as 0 — autopilots track the active item via MISSION_CURRENT.</summary>
    internal MissionItemInt ToWire(ushort seq, byte targetSystem, byte targetComponent, MavMissionType missionType) =>
        new(
            Param1: Param1, Param2: Param2, Param3: Param3, Param4: Param4,
            X: X, Y: Y, Z: Z,
            Seq: seq,
            Command: Command,
            TargetSystem: targetSystem,
            TargetComponent: targetComponent,
            Frame: Frame,
            Current: 0,
            Autocontinue: AutoContinue,
            MissionType: missionType);

    /// <summary>Reconstruct a <see cref="MissionItem"/> from a wire-shaped <see cref="MissionItemInt"/>,
    /// dropping the per-transaction fields. Used by the download state machine.</summary>
    internal static MissionItem FromWire(MissionItemInt wire) =>
        new(
            Frame: wire.Frame,
            Command: wire.Command,
            Param1: wire.Param1, Param2: wire.Param2, Param3: wire.Param3, Param4: wire.Param4,
            X: wire.X, Y: wire.Y, Z: wire.Z,
            AutoContinue: wire.Autocontinue);
}
