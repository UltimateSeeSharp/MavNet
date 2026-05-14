using FluentAssertions;
using MavNet.Core;
using MavNet.Protocol.Generated.Enums;
using MavNet.Protocol.Generated.Messages;
using Xunit;

namespace MavNet.PX4.Tests;

/// <summary>
/// Inbound telemetry updates the Vehicle's exposed properties. These tests pin the
/// unit conversions (1e7-scaled lat/lon, mm-altitude, cm/s velocity, heading/100,
/// negative battery percent normalised to 0) so a regen or refactor can't silently
/// drift them.
/// </summary>
public class VehicleTelemetryTests
{
    private static (TestDrone Drone, FakeMavlinkConnection Conn) Build()
    {
        var conn = new FakeMavlinkConnection();
        var drone = new TestDrone(conn, sys: 1, comp: 1, heartbeatTimeout: TimeSpan.FromSeconds(5));
        return (drone, conn);
    }

    [Fact]
    public void Heartbeat_updates_armed_mode_and_link()
    {
        var (drone, conn) = Build();

        conn.RaiseHeartbeat(new MavId(1, 1), new Heartbeat(
            CustomMode: 0,
            Type: MavType.Quadrotor,
            Autopilot: MavAutopilot.Px4,
            BaseMode: MavModeFlag.SafetyArmed,
            SystemStatus: MavState.Active,
            MavlinkVersion: 3));

        drone.Armed.Should().BeTrue();
        drone.LinkUp.Should().BeTrue();
        drone.VehicleType.Should().Be(MavType.Quadrotor);
        drone.LastHeartbeatAt.Should().BeAfter(DateTime.UtcNow.AddSeconds(-1));
    }

    [Fact]
    public void Heartbeat_from_wrong_sysid_is_ignored()
    {
        var (drone, conn) = Build();

        conn.RaiseHeartbeat(new MavId(99, 1), new Heartbeat(
            0, MavType.Quadrotor, MavAutopilot.Px4, MavModeFlag.SafetyArmed, MavState.Active, 3));

        drone.Armed.Should().BeFalse();
        drone.LinkUp.Should().BeFalse();
    }

    [Fact]
    public void GlobalPositionInt_scales_lat_lon_and_heading()
    {
        var (drone, conn) = Build();

        // Lat=47.123_456° → 471_234_560 (1e7 scaling). Hdg=180° → 18_000.
        conn.RaiseGlobalPosition(new MavId(1, 1), new GlobalPositionInt(
            TimeBootMs: 0, Lat: 471_234_560, Lon: -1_224_567_890,
            Alt: 100_000, RelativeAlt: 50_000,
            Vx: 300, Vy: 400, Vz: 0,
            Hdg: 18_000));

        drone.Lat.Should().BeApproximately(47.1234560, 1e-6);
        drone.Lon.Should().BeApproximately(-122.4567890, 1e-6);
        drone.Alt.Should().BeApproximately(50.0, 1e-3);
        drone.Hdg.Should().BeApproximately(180.0, 1e-3);
        drone.Vel.Should().BeApproximately(5.0, 1e-3, "sqrt(3^2 + 4^2) m/s after cm→m conversion");
    }

    [Fact]
    public void GlobalPositionInt_with_unknown_heading_reports_zero()
    {
        var (drone, conn) = Build();
        conn.RaiseGlobalPosition(new MavId(1, 1), new GlobalPositionInt(
            0, 0, 0, 0, 0, 0, 0, 0, ushort.MaxValue));

        drone.Hdg.Should().Be(0.0, "ushort.MaxValue is the MAVLink sentinel for 'no heading'");
    }

    [Fact]
    public void GpsRawInt_updates_satellites_and_fix()
    {
        var (drone, conn) = Build();
        conn.RaiseGpsRaw(new MavId(1, 1), new GpsRawInt(
            0, 0, 0, 0, 0, 0, 0, 0, GpsFixType._3dFix, 12, 0, 0, 0, 0, 0, 0));

        drone.Sats.Should().Be(12);
        drone.GpsFix.Should().Be(GpsFixType._3dFix);
    }

    [Fact]
    public void SysStatus_normalises_negative_battery_to_zero()
    {
        var (drone, conn) = Build();
        conn.RaiseSysStatus(new MavId(1, 1), new SysStatus(
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, -1, 0, 0, 0));

        drone.Battery.Should().Be(0.0, "-1 is the MAVLink sentinel for 'unknown' and shouldn't show as -1%");
    }

    [Fact]
    public void Snapshot_reflects_concurrent_field_updates_atomically()
    {
        var (drone, conn) = Build();

        conn.RaiseHeartbeat(new MavId(1, 1), new Heartbeat(
            0, MavType.Quadrotor, MavAutopilot.Px4, MavModeFlag.SafetyArmed, MavState.Active, 3));
        conn.RaiseGlobalPosition(new MavId(1, 1), new GlobalPositionInt(
            0, 100_000_000, 200_000_000, 0, 0, 0, 0, 0, 0));
        conn.RaiseSysStatus(new MavId(1, 1), new SysStatus(
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 75, 0, 0, 0));

        var snap = drone.Snapshot();
        snap.Lat.Should().BeApproximately(10.0, 1e-6);
        snap.Lon.Should().BeApproximately(20.0, 1e-6);
        snap.Armed.Should().BeTrue();
        snap.Battery.Should().Be(75);
        snap.LinkUp.Should().BeTrue();
    }

    [Fact]
    public void Vehicle_mission_state_starts_unknown()
    {
        var (drone, _) = Build();
        drone.MissionCurrentSeq.Should().Be(-1, "no MISSION_CURRENT yet");
        drone.MissionTotal.Should().Be(-1);
        drone.MissionOpaqueId.Should().Be(0u);
        drone.FenceOpaqueId.Should().Be(0u);
        drone.RallyOpaqueId.Should().Be(0u);
        drone.MissionReachedCount.Should().Be(0);
    }

    [Fact]
    public void MissionCurrent_updates_all_state_fields_and_fires_StateChanged()
    {
        var (drone, conn) = Build();
        int stateChanges = 0;
        drone.SubscribeState(() => Interlocked.Increment(ref stateChanges), StateRate.Raw);

        conn.RaiseMissionCurrent(new MavId(1, 1), new MissionCurrent(
            Seq: 2, Total: 5,
            MissionState: MissionState.Active,
            MissionMode: 1,
            MissionId: 0xAAAA, FenceId: 0xBBBB, RallyPointsId: 0xCCCC));

        drone.MissionCurrentSeq.Should().Be(2);
        drone.MissionTotal.Should().Be(5);
        drone.MissionState.Should().Be(MissionState.Active);
        drone.MissionOpaqueId.Should().Be(0xAAAAu);
        drone.FenceOpaqueId.Should().Be(0xBBBBu);
        drone.RallyOpaqueId.Should().Be(0xCCCCu);
        stateChanges.Should().Be(1, "MISSION_CURRENT must fire StateChanged so throttled subs see progress");
    }

    [Fact]
    public void MissionCurrent_from_wrong_sysid_is_ignored()
    {
        var (drone, conn) = Build();
        conn.RaiseMissionCurrent(new MavId(99, 1), new MissionCurrent(
            Seq: 2, Total: 5, MissionState: MissionState.Active, MissionMode: 0,
            MissionId: 0xAAAA, FenceId: 0, RallyPointsId: 0));

        drone.MissionCurrentSeq.Should().Be(-1, "the update came from a different vehicle");
        drone.MissionOpaqueId.Should().Be(0u);
    }

    [Fact]
    public void MissionItemReached_increments_count_and_fires_event()
    {
        var (drone, conn) = Build();
        int eventCount = 0;
        int lastSeqSeen = -1;
        drone.MissionItemReached += seq =>
        {
            Interlocked.Increment(ref eventCount);
            Interlocked.Exchange(ref lastSeqSeen, seq);
        };

        conn.RaiseMissionItemReached(new MavId(1, 1), new MissionItemReached(Seq: 0));
        conn.RaiseMissionItemReached(new MavId(1, 1), new MissionItemReached(Seq: 1));
        conn.RaiseMissionItemReached(new MavId(1, 1), new MissionItemReached(Seq: 2));

        drone.MissionReachedCount.Should().Be(3);
        eventCount.Should().Be(3);
        lastSeqSeen.Should().Be(2);
    }

    [Fact]
    public void MissionItemReached_duplicate_seq_is_deduplicated()
    {
        // PX4 occasionally re-emits the same MISSION_ITEM_REACHED on mode transitions.
        // We dedupe consecutive duplicates so the counter reflects distinct reach events.
        var (drone, conn) = Build();
        int eventCount = 0;
        drone.MissionItemReached += _ => Interlocked.Increment(ref eventCount);

        conn.RaiseMissionItemReached(new MavId(1, 1), new MissionItemReached(Seq: 1));
        conn.RaiseMissionItemReached(new MavId(1, 1), new MissionItemReached(Seq: 1)); // duplicate
        conn.RaiseMissionItemReached(new MavId(1, 1), new MissionItemReached(Seq: 2));

        drone.MissionReachedCount.Should().Be(2);
        eventCount.Should().Be(2);
    }

    [Fact]
    public void MissionItemReached_from_wrong_sysid_is_ignored()
    {
        var (drone, conn) = Build();
        conn.RaiseMissionItemReached(new MavId(99, 1), new MissionItemReached(Seq: 0));
        drone.MissionReachedCount.Should().Be(0);
    }

    [Fact]
    public void MissionItemReached_subscriber_exception_does_not_break_state()
    {
        var (drone, conn) = Build();
        drone.MissionItemReached += _ => throw new InvalidOperationException("subscriber boom");

        // Subscriber throws — count must still increment, FireStateChanged must still run.
        conn.RaiseMissionItemReached(new MavId(1, 1), new MissionItemReached(Seq: 0));
        drone.MissionReachedCount.Should().Be(1);
    }
}
