using MavNet.Core;
using MavNet.Protocol;
using MavNet.Protocol.Generated.Messages;

namespace MavNet.Transport.Udp;

/// <summary>
/// Abstraction over a MAVLink transport that publishes inbound messages as typed
/// events and accepts outbound messages via <see cref="Send{T}"/>. <see cref="MavlinkConnection"/>
/// is the production implementation; tests substitute a fake that drives the same
/// events synthetically.
/// </summary>
public interface IMavlinkConnection : IAsyncDisposable
{
    /// <summary>Inbound HEARTBEAT. Args: sender, decoded message, receive timestamp.</summary>
    event Action<MavId, Heartbeat, DateTime>? HeartbeatReceived;
    /// <summary>Inbound COMMAND_ACK.</summary>
    event Action<MavId, CommandAck, DateTime>? CommandAckReceived;
    /// <summary>Inbound GLOBAL_POSITION_INT.</summary>
    event Action<MavId, GlobalPositionInt, DateTime>? GlobalPositionIntReceived;
    /// <summary>Inbound VFR_HUD.</summary>
    event Action<MavId, VfrHud, DateTime>? VfrHudReceived;
    /// <summary>Inbound GPS_RAW_INT.</summary>
    event Action<MavId, GpsRawInt, DateTime>? GpsRawIntReceived;
    /// <summary>Inbound SYS_STATUS.</summary>
    event Action<MavId, SysStatus, DateTime>? SysStatusReceived;
    /// <summary>Inbound EXTENDED_SYS_STATE.</summary>
    event Action<MavId, ExtendedSysState, DateTime>? ExtendedSysStateReceived;
    /// <summary>Inbound MISSION_REQUEST_LIST.</summary>
    event Action<MavId, MissionRequestList, DateTime>? MissionRequestListReceived;
    /// <summary>Inbound MISSION_COUNT.</summary>
    event Action<MavId, MissionCount, DateTime>? MissionCountReceived;
    /// <summary>Inbound MISSION_CLEAR_ALL.</summary>
    event Action<MavId, MissionClearAll, DateTime>? MissionClearAllReceived;
    /// <summary>Inbound MISSION_ITEM_REACHED.</summary>
    event Action<MavId, MissionItemReached, DateTime>? MissionItemReachedReceived;
    /// <summary>Inbound MISSION_ACK.</summary>
    event Action<MavId, MissionAck, DateTime>? MissionAckReceived;
    /// <summary>Inbound MISSION_CURRENT.</summary>
    event Action<MavId, MissionCurrent, DateTime>? MissionCurrentReceived;
    /// <summary>Inbound MISSION_REQUEST (deprecated, but ArduPilot still sends it — respond with a MISSION_ITEM_INT).</summary>
    event Action<MavId, MissionRequest, DateTime>? MissionRequestReceived;
    /// <summary>Inbound MISSION_REQUEST_INT.</summary>
    event Action<MavId, MissionRequestInt, DateTime>? MissionRequestIntReceived;
    /// <summary>Inbound MISSION_ITEM_INT.</summary>
    event Action<MavId, MissionItemInt, DateTime>? MissionItemIntReceived;

    /// <summary>Send a generated MAVLink message. Performs MAVLink v2 wire truncation and CRC stamping.</summary>
    void Send<T>(T message) where T : IMavlinkMessage<T>;
}
