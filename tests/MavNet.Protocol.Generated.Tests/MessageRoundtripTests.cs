using System.Buffers.Binary;
using FluentAssertions;
using MavNet.Protocol;
using MavNet.Protocol.Generated.Enums;
using MavNet.Protocol.Generated.Messages;
using Xunit;

namespace MavNet.Protocol.Generated.Tests;

/// <summary>
/// Roundtrip tests for every allowlisted message struct. Catches off-by-one slice
/// boundaries, wrong-endian writes, and stale offsets after a regen. The byte-level
/// layout assertions are deliberately redundant with the encode source — they pin
/// the wire format so regenerating with a buggy emitter fails loudly.
/// </summary>
public class MessageRoundtripTests
{
    private static T Roundtrip<T>(T msg) where T : IMavlinkMessage<T>
    {
        System.Span<byte> buf = stackalloc byte[T.MaxPayloadLength];
        msg.Encode(buf);
        return T.Decode(buf);
    }

    [Fact]
    public void Heartbeat_roundtrip_preserves_all_fields()
    {
        var original = new Heartbeat(
            CustomMode: 0x12345678,
            Type: (MavType)2,
            Autopilot: (MavAutopilot)12,
            BaseMode: (MavModeFlag)0x81,
            SystemStatus: (MavState)4,
            MavlinkVersion: 3);

        System.Span<byte> buf = stackalloc byte[Heartbeat.MaxPayloadLength];
        original.Encode(buf);

        BinaryPrimitives.ReadUInt32LittleEndian(buf[0..4]).Should().Be(0x12345678u);
        buf[4].Should().Be((byte)2);
        buf[5].Should().Be((byte)12);
        buf[6].Should().Be((byte)0x81);
        buf[7].Should().Be((byte)4);
        buf[8].Should().Be((byte)3);

        Heartbeat.Decode(buf).Should().Be(original);
    }

    [Fact]
    public void CommandLong_roundtrip_preserves_all_seven_floats()
    {
        var original = new CommandLong(
            Param1: 1.5f, Param2: -2.25f, Param3: 3.75f,
            Param4: 4f,   Param5: -5.5f,  Param6: 6.125f, Param7: 100f,
            Command: (MavCmd)22, TargetSystem: 1, TargetComponent: 1, Confirmation: 0);

        Roundtrip(original).Should().Be(original);
    }

    [Fact]
    public void CommandAck_roundtrip_full_length()
    {
        var original = new CommandAck(
            Command: (MavCmd)400,
            Result: (MavResult)0,
            Progress: 42,
            ResultParam2: -123456,
            TargetSystem: 1,
            TargetComponent: 1);

        Roundtrip(original).Should().Be(original);
    }

    [Fact]
    public void CommandAck_decode_at_min_length_zero_fills_extensions()
    {
        // COMMAND_ACK: Min=3, Max=10. A truncated 3-byte payload should still decode
        // (extensions arrive zero-filled). This mirrors what PX4 sends on the wire.
        var truncated = new byte[] { 0x10, 0x00, 0x05 }; // command=0x0010, result=5

        var decoded = CommandAck.Decode(truncated);

        decoded.Command.Should().Be((MavCmd)0x0010);
        decoded.Result.Should().Be((MavResult)5);
        decoded.Progress.Should().Be((byte)0);
        decoded.ResultParam2.Should().Be(0);
        decoded.TargetSystem.Should().Be((byte)0);
        decoded.TargetComponent.Should().Be((byte)0);
    }

    [Fact]
    public void SysStatus_roundtrip_full_length()
    {
        var original = new SysStatus(
            OnboardControlSensorsPresent: (MavSysStatusSensor)0x0EAD0EEF,
            OnboardControlSensorsEnabled: (MavSysStatusSensor)0x0AFE0ABE,
            OnboardControlSensorsHealth:  (MavSysStatusSensor)0x12345678,
            Load: 500, VoltageBattery: 12500, CurrentBattery: -1,
            DropRateComm: 0, ErrorsComm: 0,
            ErrorsCount1: 1, ErrorsCount2: 2, ErrorsCount3: 3, ErrorsCount4: 4,
            BatteryRemaining: 87,
            OnboardControlSensorsPresentExtended: (MavSysStatusSensorExtended)0x0A0A0A0A,
            OnboardControlSensorsEnabledExtended: (MavSysStatusSensorExtended)0x0B0B0B0B,
            OnboardControlSensorsHealthExtended:  (MavSysStatusSensorExtended)0x0C0C0C0C);

        Roundtrip(original).Should().Be(original);
    }

    [Fact]
    public void SysStatus_decode_at_min_length_zero_fills_extension_dwords()
    {
        // SysStatus Min=31 (everything through BatteryRemaining), Max=43 (with 3 extension u32s).
        System.Span<byte> buf = stackalloc byte[SysStatus.MaxPayloadLength];
        var withExt = new SysStatus(
            (MavSysStatusSensor)1, (MavSysStatusSensor)2, (MavSysStatusSensor)3,
            10, 11, 12, 13, 14, 15, 16, 17, 18, 50,
            (MavSysStatusSensorExtended)0xDEAD, (MavSysStatusSensorExtended)0xBEEF, (MavSysStatusSensorExtended)0x1234);
        withExt.Encode(buf);

        // Decode only the first 31 bytes — extensions must come back zero.
        var decoded = SysStatus.Decode(buf[..SysStatus.MinPayloadLength]);

        decoded.OnboardControlSensorsPresent.Should().Be((MavSysStatusSensor)1);
        decoded.BatteryRemaining.Should().Be((sbyte)50);
        decoded.OnboardControlSensorsPresentExtended.Should().Be((MavSysStatusSensorExtended)0);
        decoded.OnboardControlSensorsEnabledExtended.Should().Be((MavSysStatusSensorExtended)0);
        decoded.OnboardControlSensorsHealthExtended.Should().Be((MavSysStatusSensorExtended)0);
    }

    [Fact]
    public void GlobalPositionInt_roundtrip_preserves_signed_ints()
    {
        var original = new GlobalPositionInt(
            TimeBootMs: 123_456_789u,
            Lat: -47_123_456,    // Sydney-ish latitude * 1e7, negative for southern hemisphere
            Lon:  151_234_567,
            Alt: 100_000,
            RelativeAlt: 50_000,
            Vx: -10, Vy: 20, Vz: -5,
            Hdg: 18000);

        Roundtrip(original).Should().Be(original);
    }

    [Fact]
    public void VfrHud_roundtrip_preserves_floats_and_negative_heading()
    {
        var original = new VfrHud(
            Airspeed: 12.5f, Groundspeed: 11.75f, Alt: 123.4f, Climb: -0.5f,
            Heading: -45, Throttle: 65);

        Roundtrip(original).Should().Be(original);
    }

    [Fact]
    public void GpsRawInt_roundtrip_full_length()
    {
        var original = new GpsRawInt(
            TimeUsec: 0xABCDEF0123456789UL,
            Lat: 47_123_456, Lon: -122_456_789, Alt: 50_000,
            Eph: 100, Epv: 200, Vel: 300, Cog: 18000,
            FixType: (GpsFixType)3, SatellitesVisible: 14,
            AltEllipsoid: 49_000,
            HAcc: 1500, VAcc: 2500, VelAcc: 100, HdgAcc: 200,
            Yaw: 9000);

        Roundtrip(original).Should().Be(original);
    }

    [Fact]
    public void ExtendedSysState_roundtrip()
    {
        var original = new ExtendedSysState(
            VtolState: (MavVtolState)2,
            LandedState: (MavLandedState)1);

        Roundtrip(original).Should().Be(original);
    }

    [Fact]
    public void MissionRequestList_roundtrip()
    {
        var original = new MissionRequestList(
            TargetSystem: 1, TargetComponent: 1, MissionType: MavMissionType.Mission);

        Roundtrip(original).Should().Be(original);
    }

    [Fact]
    public void MissionCount_roundtrip_preserves_opaque_id()
    {
        var original = new MissionCount(
            Count: 7, TargetSystem: 1, TargetComponent: 1,
            MissionType: MavMissionType.Fence,
            OpaqueId: 0xDEADBEEFu);

        Roundtrip(original).Should().Be(original);
    }

    [Fact]
    public void MissionClearAll_roundtrip()
    {
        var original = new MissionClearAll(
            TargetSystem: 1, TargetComponent: 1, MissionType: MavMissionType.Rally);

        Roundtrip(original).Should().Be(original);
    }

    [Fact]
    public void MissionItemReached_roundtrip()
    {
        var original = new MissionItemReached(Seq: 42);

        Roundtrip(original).Should().Be(original);
    }

    [Fact]
    public void MissionAck_roundtrip_preserves_opaque_id()
    {
        var original = new MissionAck(
            TargetSystem: 1, TargetComponent: 1,
            Type: MavMissionResult.MavMissionAccepted,
            MissionType: MavMissionType.Mission,
            OpaqueId: 0xCAFEBABEu);

        Roundtrip(original).Should().Be(original);
    }

    [Fact]
    public void MissionCurrent_roundtrip_preserves_all_three_plan_ids()
    {
        var original = new MissionCurrent(
            Seq: 3, Total: 10,
            MissionState: (MissionState)2, MissionMode: 1,
            MissionId: 0x11111111u, FenceId: 0x22222222u, RallyPointsId: 0x33333333u);

        Roundtrip(original).Should().Be(original);
    }

    [Fact]
    public void MissionRequest_roundtrip_deprecated_but_still_decoded()
    {
        // MISSION_REQUEST (msgid 40) is deprecated per spec but ArduPilot still sends it.
        // We must still decode it correctly to know which item to respond with.
        var original = new MissionRequest(
            Seq: 5, TargetSystem: 1, TargetComponent: 1, MissionType: MavMissionType.Mission);

        Roundtrip(original).Should().Be(original);
    }

    [Fact]
    public void MissionRequestInt_roundtrip()
    {
        var original = new MissionRequestInt(
            Seq: 5, TargetSystem: 1, TargetComponent: 1, MissionType: MavMissionType.Mission);

        Roundtrip(original).Should().Be(original);
    }

    [Fact]
    public void MissionItemInt_roundtrip_full_layout()
    {
        // Per MAVLink spec, MISSION_ITEM_INT uses int32 × 1e7 for global lat/lon.
        // Verify all 15 fields round-trip including the signed ints and the float Z.
        var original = new MissionItemInt(
            Param1: 0f, Param2: 5f, Param3: 0f, Param4: float.NaN,    // NaN for "current heading" is common
            X: -47_123_456, Y: 151_234_567, Z: 50f,
            Seq: 2,
            Command: (MavCmd)16,                                       // MAV_CMD_NAV_WAYPOINT
            TargetSystem: 1, TargetComponent: 1,
            Frame: (MavFrame)3,                                         // GLOBAL_RELATIVE_ALT_INT
            Current: 0, Autocontinue: 1,
            MissionType: MavMissionType.Mission);

        System.Span<byte> buf = stackalloc byte[MissionItemInt.MaxPayloadLength];
        original.Encode(buf);
        var decoded = MissionItemInt.Decode(buf);

        // float.NaN does not equal itself, so compare it separately and the rest via field-wise equality.
        decoded.Param1.Should().Be(0f);
        decoded.Param2.Should().Be(5f);
        decoded.Param3.Should().Be(0f);
        float.IsNaN(decoded.Param4).Should().BeTrue();
        decoded.X.Should().Be(-47_123_456);
        decoded.Y.Should().Be(151_234_567);
        decoded.Z.Should().Be(50f);
        decoded.Seq.Should().Be((ushort)2);
        decoded.Command.Should().Be((MavCmd)16);
        decoded.TargetSystem.Should().Be((byte)1);
        decoded.TargetComponent.Should().Be((byte)1);
        decoded.Frame.Should().Be((MavFrame)3);
        decoded.Current.Should().Be((byte)0);
        decoded.Autocontinue.Should().Be((byte)1);
        decoded.MissionType.Should().Be(MavMissionType.Mission);
    }
}
