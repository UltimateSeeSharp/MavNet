using System.Net;
using FluentAssertions;
using MavNet.Core;
using MavNet.Protocol.Generated.Enums;
using MavNet.Protocol.Generated.Messages;
using MavNet.Transport.Udp;
using Xunit;

namespace MavNet.Transport.Udp.Tests;

/// <summary>
/// Round-trips frames through the UDP receive loop. The peer crafts a valid frame
/// and sends it to the connection's bound port; the connection must decode and fire
/// the matching strongly-typed event with a correct <see cref="MavId"/>.
/// </summary>
public class MavlinkConnectionDispatchTests
{
    private const int LongHeartbeat = 60_000; // suppress GCS heartbeat noise during tests

    private static MavlinkConnection StartConnection(LoopbackPeer peer)
    {
        var conn = new MavlinkConnection(
            localBind: new IPEndPoint(IPAddress.Loopback, 0),
            remote: peer.EndPoint,
            selfSystemId: 255,
            selfComponentId: 190,
            gcsHeartbeatIntervalMs: LongHeartbeat);
        conn.Start();
        return conn;
    }

    private static async Task<T> WaitForEvent<T>(TaskCompletionSource<T> tcs)
    {
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(System.TimeSpan.FromSeconds(2)));
        completed.Should().Be(tcs.Task, "the event should have fired within 2s");
        return await tcs.Task;
    }

    [Fact]
    public async Task Heartbeat_inbound_fires_HeartbeatReceived_with_correct_MavId()
    {
        using var peer = new LoopbackPeer();
        await using var conn = StartConnection(peer);
        var tcs = new TaskCompletionSource<(MavId Sender, Heartbeat Msg)>();
        conn.HeartbeatReceived += (id, msg, _) => tcs.TrySetResult((id, msg));

        // Drain the first heartbeat the connection emits on Start so it doesn't pollute timing.
        peer.Receive(System.TimeSpan.FromMilliseconds(100));

        var hb = new Heartbeat(0x12345678, MavType.Quadrotor, MavAutopilot.Px4, MavModeFlag.SafetyArmed, MavState.Active, 3);
        var payload = new byte[Heartbeat.MaxPayloadLength];
        hb.Encode(payload);
        var frame = FrameBuilder.BuildFrame(
            msgId: Heartbeat.MsgId, crcExtra: Heartbeat.CrcExtra, payload: payload,
            seq: 7, sysid: 1, compid: 1);
        peer.SendTo(frame, conn.LocalEndPoint!);

        var (sender, decoded) = await WaitForEvent(tcs);
        sender.Should().Be(new MavId(1, 1));
        decoded.Should().Be(hb);
    }

    [Fact]
    public async Task CommandAck_inbound_fires_event()
    {
        using var peer = new LoopbackPeer();
        await using var conn = StartConnection(peer);
        peer.Receive(System.TimeSpan.FromMilliseconds(100));

        var tcs = new TaskCompletionSource<CommandAck>();
        conn.CommandAckReceived += (_, ack, _) => tcs.TrySetResult(ack);

        var ack = new CommandAck((MavCmd)400, (MavResult)0, 0, 0, 1, 1);
        var payload = new byte[CommandAck.MaxPayloadLength];
        ack.Encode(payload);
        var frame = FrameBuilder.BuildFrame(
            CommandAck.MsgId, CommandAck.CrcExtra, payload, seq: 1, sysid: 1, compid: 1);
        peer.SendTo(frame, conn.LocalEndPoint!);

        (await WaitForEvent(tcs)).Should().Be(ack);
    }

    [Fact]
    public async Task GlobalPositionInt_inbound_fires_event()
    {
        using var peer = new LoopbackPeer();
        await using var conn = StartConnection(peer);
        peer.Receive(System.TimeSpan.FromMilliseconds(100));

        var tcs = new TaskCompletionSource<GlobalPositionInt>();
        conn.GlobalPositionIntReceived += (_, msg, _) => tcs.TrySetResult(msg);

        var msg = new GlobalPositionInt(100, -47_000_000, 151_000_000, 50_000, 25_000, 1, 2, 3, 9000);
        var payload = new byte[GlobalPositionInt.MaxPayloadLength];
        msg.Encode(payload);
        var frame = FrameBuilder.BuildFrame(
            GlobalPositionInt.MsgId, GlobalPositionInt.CrcExtra, payload, seq: 1, sysid: 1, compid: 1);
        peer.SendTo(frame, conn.LocalEndPoint!);

        (await WaitForEvent(tcs)).Should().Be(msg);
    }

    [Fact]
    public async Task Subscriber_exception_does_not_kill_receive_loop()
    {
        // Invariant: when a subscriber throws while handling frame N, the receive loop
        // must continue and dispatch frame N+1. (Other subscribers on the same multicast
        // chain may be skipped for frame N — that's just how multicast delegates work.)
        using var peer = new LoopbackPeer();
        await using var conn = StartConnection(peer);
        peer.Receive(System.TimeSpan.FromMilliseconds(100));

        var receivedFrames = 0;
        var bothSeen = new TaskCompletionSource<bool>();
        conn.HeartbeatReceived += (_, _, _) =>
        {
            var n = System.Threading.Interlocked.Increment(ref receivedFrames);
            if (n >= 2) bothSeen.TrySetResult(true);
            throw new System.InvalidOperationException("subscriber boom");
        };

        var hb = new Heartbeat(0, MavType.Gcs, MavAutopilot.Invalid, MavModeFlag.CustomModeEnabled, MavState.Active, 3);
        var payload = new byte[Heartbeat.MaxPayloadLength];
        hb.Encode(payload);
        var frame1 = FrameBuilder.BuildFrame(Heartbeat.MsgId, Heartbeat.CrcExtra, payload, seq: 1);
        var frame2 = FrameBuilder.BuildFrame(Heartbeat.MsgId, Heartbeat.CrcExtra, payload, seq: 2);

        peer.SendTo(frame1, conn.LocalEndPoint!);
        await Task.Delay(50);
        peer.SendTo(frame2, conn.LocalEndPoint!);

        await WaitForEvent(bothSeen);
        receivedFrames.Should().Be(2);
    }

    [Fact]
    public async Task Frames_for_unknown_msgids_are_silently_ignored()
    {
        using var peer = new LoopbackPeer();
        await using var conn = StartConnection(peer);
        peer.Receive(System.TimeSpan.FromMilliseconds(100));

        bool anyEventFired = false;
        conn.HeartbeatReceived         += (_, _, _) => anyEventFired = true;
        conn.CommandAckReceived        += (_, _, _) => anyEventFired = true;
        conn.GlobalPositionIntReceived += (_, _, _) => anyEventFired = true;

        // ATTITUDE (msgid=30, crc_extra=39) is in the registry but not in the dispatcher.
        var payload = new byte[28];
        var frame = FrameBuilder.BuildFrame(
            msgId: 30, crcExtra: 39, payload: payload, seq: 1, sysid: 1, compid: 1);
        peer.SendTo(frame, conn.LocalEndPoint!);

        await Task.Delay(100);
        anyEventFired.Should().BeFalse("ATTITUDE is not in the dispatcher today");
    }

    [Fact]
    public async Task DisposeAsync_is_idempotent()
    {
        using var peer = new LoopbackPeer();
        var conn = StartConnection(peer);

        await conn.DisposeAsync();
        var act = async () => await conn.DisposeAsync();
        await act.Should().NotThrowAsync();
    }
}
