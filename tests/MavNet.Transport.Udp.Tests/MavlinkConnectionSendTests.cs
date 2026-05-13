using System.Net;
using FluentAssertions;
using MavNet.Protocol;
using MavNet.Protocol.Generated.Enums;
using MavNet.Protocol.Generated.Messages;
using MavNet.Transport.Udp;
using Xunit;

namespace MavNet.Transport.Udp.Tests;

/// <summary>
/// Verifies that <see cref="MavlinkConnection.Send{T}"/> emits well-formed MAVLink v2
/// frames on the wire — correct CRC, correct sequence increment, correct MAVLink v2
/// trailing-zero truncation. These are the bytes PX4 sees.
/// </summary>
public class MavlinkConnectionSendTests
{
    private static MavlinkConnection BuildConnection(LoopbackPeer peer) =>
        new(
            localBind: new IPEndPoint(IPAddress.Loopback, 0),
            remote: peer.EndPoint,
            selfSystemId: 255,
            selfComponentId: 190,
            gcsHeartbeatIntervalMs: 60_000);

    [Fact]
    public async Task Send_emits_a_frame_that_TryDecode_accepts()
    {
        using var peer = new LoopbackPeer();
        await using var conn = BuildConnection(peer);

        var hb = new Heartbeat(0, MavType.Gcs, MavAutopilot.Invalid, MavModeFlag.CustomModeEnabled, MavState.Active, 3);
        conn.Send(hb);

        var bytes = peer.Receive(System.TimeSpan.FromSeconds(1));
        bytes.Should().NotBeNull();

        MavlinkFrame.TryDecode(bytes!, out var frame).Should().BeTrue();
        frame.MessageId.Should().Be(Heartbeat.MsgId);
        frame.SystemId.Should().Be((byte)255);
        frame.ComponentId.Should().Be((byte)190);
    }

    [Fact]
    public async Task Send_truncates_trailing_zero_extension_bytes()
    {
        // COMMAND_ACK Min=3, Max=10. If the extension fields (ResultParam2, TargetSystem,
        // TargetComponent) are all zero, the wire payload should be just 3 bytes.
        using var peer = new LoopbackPeer();
        await using var conn = BuildConnection(peer);

        var ack = new CommandAck(
            Command: (MavCmd)400, Result: (MavResult)1,
            Progress: 0, ResultParam2: 0, TargetSystem: 0, TargetComponent: 0);
        conn.Send(ack);

        var bytes = peer.Receive(System.TimeSpan.FromSeconds(1));
        bytes.Should().NotBeNull();
        // Frame = 10 header + payload + 2 crc. Truncated payload = 3 bytes → total 15.
        bytes!.Length.Should().Be(10 + CommandAck.MinPayloadLength + 2);
        bytes[1].Should().Be(CommandAck.MinPayloadLength);
    }

    [Fact]
    public async Task Send_keeps_full_length_when_extension_bytes_are_non_zero()
    {
        using var peer = new LoopbackPeer();
        await using var conn = BuildConnection(peer);

        // ProgressByte=42 sits at offset 3 (inside the truncation range). TargetComponent=1
        // sits at offset 9 — its non-zero value should force the full-length send.
        var ack = new CommandAck(
            Command: (MavCmd)400, Result: (MavResult)1,
            Progress: 0, ResultParam2: 0, TargetSystem: 0, TargetComponent: 1);
        conn.Send(ack);

        var bytes = peer.Receive(System.TimeSpan.FromSeconds(1));
        bytes.Should().NotBeNull();
        bytes![1].Should().Be(CommandAck.MaxPayloadLength);
    }

    [Fact]
    public async Task Truncation_never_goes_below_MinPayloadLength()
    {
        // All-zero CommandAck: every byte after offset 0 is zero, but the wire
        // payload must never shrink below Min (3).
        using var peer = new LoopbackPeer();
        await using var conn = BuildConnection(peer);

        var ack = new CommandAck(
            Command: 0, Result: 0,
            Progress: 0, ResultParam2: 0, TargetSystem: 0, TargetComponent: 0);
        conn.Send(ack);

        var bytes = peer.Receive(System.TimeSpan.FromSeconds(1));
        bytes!.Length.Should().Be(10 + CommandAck.MinPayloadLength + 2);
    }

    [Fact]
    public async Task Sequence_number_increments_per_frame()
    {
        using var peer = new LoopbackPeer();
        await using var conn = BuildConnection(peer);

        var hb = new Heartbeat(0, MavType.Gcs, MavAutopilot.Invalid, MavModeFlag.CustomModeEnabled, MavState.Active, 3);
        conn.Send(hb);
        conn.Send(hb);
        conn.Send(hb);

        var f1 = peer.Receive(System.TimeSpan.FromSeconds(1))!;
        var f2 = peer.Receive(System.TimeSpan.FromSeconds(1))!;
        var f3 = peer.Receive(System.TimeSpan.FromSeconds(1))!;

        var s1 = f1[4]; var s2 = f2[4]; var s3 = f3[4];
        // Sequence byte (offset 4) increments mod 256.
        ((byte)(s2 - s1)).Should().Be((byte)1);
        ((byte)(s3 - s2)).Should().Be((byte)1);
    }

    [Fact]
    public async Task GCS_heartbeat_fires_after_Start()
    {
        using var peer = new LoopbackPeer();
        // Heartbeat interval=50ms; receive within a generous window.
        await using var conn = new MavlinkConnection(
            localBind: new IPEndPoint(IPAddress.Loopback, 0),
            remote: peer.EndPoint,
            selfSystemId: 255,
            selfComponentId: 190,
            gcsHeartbeatIntervalMs: 50);
        conn.Start();

        var bytes = peer.Receive(System.TimeSpan.FromSeconds(2));

        bytes.Should().NotBeNull("the GCS heartbeat timer should have fired at least once");
        MavlinkFrame.TryDecode(bytes!, out var frame).Should().BeTrue();
        frame.MessageId.Should().Be(Heartbeat.MsgId);
    }
}
