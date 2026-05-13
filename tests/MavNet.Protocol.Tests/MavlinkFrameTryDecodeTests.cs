using FluentAssertions;
using MavNet.Protocol;
using Xunit;

namespace MavNet.Protocol.Tests;

/// <summary>
/// One test per drop branch in <see cref="MavlinkFrame.TryDecode"/>. The decoder is the
/// chokepoint — anything that gets through here is interpreted as a real PX4 message,
/// so silent acceptance of bad bytes is the most dangerous class of regression.
/// </summary>
public class MavlinkFrameTryDecodeTests
{
    [Fact]
    public void Valid_heartbeat_round_trips()
    {
        var frame = FrameBuilder.BuildHeartbeat(seq: 42, sysid: 1, compid: 200);

        MavlinkFrame.TryDecode(frame, out var decoded).Should().BeTrue();

        decoded.PayloadLength.Should().Be((byte)9);
        decoded.Sequence.Should().Be((byte)42);
        decoded.SystemId.Should().Be((byte)1);
        decoded.ComponentId.Should().Be((byte)200);
        decoded.MessageId.Should().Be(0u);
        decoded.IncompatFlags.Should().Be((byte)0);
        decoded.CompatFlags.Should().Be((byte)0);
        decoded.Payload.Length.Should().Be(9);
        decoded.Payload[0].Should().Be((byte)0x01);
        decoded.Payload[8].Should().Be((byte)0x09);
    }

    [Fact]
    public void Returns_false_when_buffer_too_short()
    {
        // Minimum v2 frame is 12 bytes (header 10 + crc 2, zero-byte payload).
        MavlinkFrame.TryDecode(new byte[11], out _).Should().BeFalse();
        MavlinkFrame.TryDecode(System.ReadOnlySpan<byte>.Empty, out _).Should().BeFalse();
    }

    [Fact]
    public void Returns_false_for_v1_magic()
    {
        var frame = FrameBuilder.BuildHeartbeat();
        frame[0] = 0xFE; // v1 magic — unsupported.
        MavlinkFrame.TryDecode(frame, out _).Should().BeFalse();
    }

    [Fact]
    public void Returns_false_for_signed_frames()
    {
        // We deliberately don't support signing yet; signed frames must be dropped, not
        // silently accepted with the signature treated as garbage payload.
        var frame = FrameBuilder.BuildHeartbeat(incompat: 0x01);
        MavlinkFrame.TryDecode(frame, out _).Should().BeFalse();
    }

    [Fact]
    public void Returns_false_for_unknown_incompat_bits()
    {
        // Spec: receivers MUST drop frames whose incompat flags they don't recognise,
        // otherwise we may forward a frame whose new-bit-meaning we can't preserve.
        var frame = FrameBuilder.BuildHeartbeat(incompat: 0x02);
        MavlinkFrame.TryDecode(frame, out _).Should().BeFalse();

        var frame2 = FrameBuilder.BuildHeartbeat(incompat: 0x80);
        MavlinkFrame.TryDecode(frame2, out _).Should().BeFalse();
    }

    [Fact]
    public void Returns_false_when_payload_length_exceeds_buffer()
    {
        var frame = FrameBuilder.BuildHeartbeat();
        // Truncate one byte off the end; declared length still says 9 → buffer too short.
        var truncated = frame.AsSpan(0, frame.Length - 1).ToArray();
        MavlinkFrame.TryDecode(truncated, out _).Should().BeFalse();
    }

    [Fact]
    public void Returns_false_for_unknown_msgid()
    {
        // 0xFFFFFE is reserved-unused in the generated registry. We can't look up
        // a CrcExtra for it, so the frame must be dropped rather than accepted blindly.
        var frame = FrameBuilder.BuildFrame(
            msgId: 0x00FFFFFE,
            crcExtra: 0,
            payload: new byte[9]);
        MavlinkFrame.TryDecode(frame, out _).Should().BeFalse();
    }

    [Fact]
    public void Returns_false_when_payload_length_out_of_range_for_msgid()
    {
        // HEARTBEAT has Min=Max=9. Build a frame with len=8 (below floor) and matching CRC.
        var shortPayload = new byte[8];
        var frame = FrameBuilder.BuildFrame(msgId: 0, crcExtra: 50, payload: shortPayload);
        MavlinkFrame.TryDecode(frame, out _).Should().BeFalse();

        // And len=10 (above ceiling).
        var longPayload = new byte[10];
        var frame2 = FrameBuilder.BuildFrame(msgId: 0, crcExtra: 50, payload: longPayload);
        MavlinkFrame.TryDecode(frame2, out _).Should().BeFalse();
    }

    [Fact]
    public void Returns_false_when_crc_low_byte_flipped()
    {
        var frame = FrameBuilder.BuildHeartbeat();
        frame[^2] ^= 0x01;
        MavlinkFrame.TryDecode(frame, out _).Should().BeFalse();
    }

    [Fact]
    public void Returns_false_when_crc_high_byte_flipped()
    {
        var frame = FrameBuilder.BuildHeartbeat();
        frame[^1] ^= 0x80;
        MavlinkFrame.TryDecode(frame, out _).Should().BeFalse();
    }

    [Fact]
    public void Returns_false_when_payload_corrupted_without_crc_update()
    {
        // Mutating any header or payload byte without recomputing the CRC must fail
        // validation — this is the whole point of the per-message CRC_EXTRA seed.
        var frame = FrameBuilder.BuildHeartbeat();
        frame[10] ^= 0xFF; // flip the first payload byte
        MavlinkFrame.TryDecode(frame, out _).Should().BeFalse();
    }

    [Fact]
    public void Accepts_unused_compat_bits_silently()
    {
        // Compat flags are explicitly allowed to carry unknown bits (only incompat
        // flags trigger a drop). Verify we don't accidentally tighten that.
        var frame = FrameBuilder.BuildHeartbeat(compat: 0xFF);
        MavlinkFrame.TryDecode(frame, out var decoded).Should().BeTrue();
        decoded.CompatFlags.Should().Be((byte)0xFF);
    }

    [Fact]
    public void Decodes_messages_with_extensions_at_min_length()
    {
        // COMMAND_ACK: Min=3, Max=10. A wire-truncated 3-byte payload must still verify.
        var truncatedAck = new byte[] { 0x10, 0x00, 0x04 }; // command=0x0010, result=0x04
        var frame = FrameBuilder.BuildFrame(msgId: 77, crcExtra: 143, payload: truncatedAck);

        MavlinkFrame.TryDecode(frame, out var decoded).Should().BeTrue();
        decoded.MessageId.Should().Be(77u);
        decoded.PayloadLength.Should().Be((byte)3);
    }
}
