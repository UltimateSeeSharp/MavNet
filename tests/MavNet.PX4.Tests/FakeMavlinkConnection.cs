using MavNet.Core;
using MavNet.Protocol;
using MavNet.Protocol.Generated.Messages;
using MavNet.Transport.Udp;

namespace MavNet.PX4.Tests;

/// <summary>
/// In-memory <see cref="IMavlinkConnection"/> for testing Vehicle/Drone without UDP.
/// Tests call <c>Raise*</c> methods to fire inbound events synchronously and inspect
/// <see cref="SentMessages"/> for what the system under test tried to send.
/// </summary>
internal sealed class FakeMavlinkConnection : IMavlinkConnection
{
    public List<object> SentMessages { get; } = new();

    public event Action<MavId, Heartbeat, DateTime>? HeartbeatReceived;
    public event Action<MavId, CommandAck, DateTime>? CommandAckReceived;
    public event Action<MavId, GlobalPositionInt, DateTime>? GlobalPositionIntReceived;
    public event Action<MavId, VfrHud, DateTime>? VfrHudReceived;
    public event Action<MavId, GpsRawInt, DateTime>? GpsRawIntReceived;
    public event Action<MavId, SysStatus, DateTime>? SysStatusReceived;
    public event Action<MavId, ExtendedSysState, DateTime>? ExtendedSysStateReceived;

    public void Send<T>(T message) where T : IMavlinkMessage<T> => SentMessages.Add(message!);

    public void RaiseHeartbeat(MavId from, Heartbeat hb)             => HeartbeatReceived?.Invoke(from, hb, DateTime.UtcNow);
    public void RaiseCommandAck(MavId from, CommandAck ack)          => CommandAckReceived?.Invoke(from, ack, DateTime.UtcNow);
    public void RaiseGlobalPosition(MavId from, GlobalPositionInt g) => GlobalPositionIntReceived?.Invoke(from, g, DateTime.UtcNow);
    public void RaiseVfrHud(MavId from, VfrHud v)                    => VfrHudReceived?.Invoke(from, v, DateTime.UtcNow);
    public void RaiseGpsRaw(MavId from, GpsRawInt g)                 => GpsRawIntReceived?.Invoke(from, g, DateTime.UtcNow);
    public void RaiseSysStatus(MavId from, SysStatus s)              => SysStatusReceived?.Invoke(from, s, DateTime.UtcNow);
    public void RaiseExtendedSysState(MavId from, ExtendedSysState e)=> ExtendedSysStateReceived?.Invoke(from, e, DateTime.UtcNow);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
