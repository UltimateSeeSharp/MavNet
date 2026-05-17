using FluentAssertions;
using MavNet.Protocol;
using MavNet.Protocol.Generated;
using Xunit;

namespace MavNet.Protocol.Tests;

public class MessageRegistryTests
{
    [Theory]
    [InlineData(0u, "HEARTBEAT", (byte)50, (byte)9, (byte)9)]
    [InlineData(76u, "COMMAND_LONG", (byte)152, (byte)33, (byte)33)]
    [InlineData(77u, "COMMAND_ACK", (byte)143, (byte)3, (byte)10)]
    [InlineData(33u, "GLOBAL_POSITION_INT", (byte)104, (byte)28, (byte)28)]
    [InlineData(1u, "SYS_STATUS", (byte)124, (byte)31, (byte)43)]
    public void Known_messages_resolve_with_expected_metadata(
        uint msgId, string name, byte crcExtra, byte min, byte max)
    {
        MessageRegistry.TryGet(msgId, out var info).Should().BeTrue();
        info.MsgId.Should().Be(msgId);
        info.Name.Should().Be(name);
        info.CrcExtra.Should().Be(crcExtra);
        info.MinPayloadLength.Should().Be(min);
        info.MaxPayloadLength.Should().Be(max);
    }

    [Fact]
    public void Unknown_msgid_returns_false()
    {
        MessageRegistry.TryGet(0x00FFFFFE, out _).Should().BeFalse();
        MessageRegistry.Contains(0x00FFFFFE).Should().BeFalse();
    }

    [Fact]
    public void All_messages_have_consistent_length_range()
    {
        foreach (var info in MessageRegistry.All)
        {
            info.MinPayloadLength.Should().BeLessOrEqualTo(info.MaxPayloadLength,
                $"{info.Name} (id={info.MsgId}) must have Min <= Max");
            info.MaxPayloadLength.Should().BeLessOrEqualTo((byte)255,
                $"{info.Name} must fit in a v2 payload byte");
        }
    }

    [Fact]
    public void Extension_messages_have_min_less_than_max()
    {
        MessageRegistry.TryGet(CommandAckId, out var ack).Should().BeTrue();
        ack.Flags.HasFlag(MessageCapabilities.HasExtensions).Should().BeTrue();
        ack.MinPayloadLength.Should().BeLessThan(ack.MaxPayloadLength);

        MessageRegistry.TryGet(SysStatusId, out var sys).Should().BeTrue();
        sys.Flags.HasFlag(MessageCapabilities.HasExtensions).Should().BeTrue();
        sys.MinPayloadLength.Should().BeLessThan(sys.MaxPayloadLength);
    }

    [Fact]
    public void IsValidPayloadLength_enforces_inclusive_range()
    {
        MessageRegistry.TryGet(CommandAckId, out var ack).Should().BeTrue();
        ack.IsValidPayloadLength(ack.MinPayloadLength).Should().BeTrue();
        ack.IsValidPayloadLength(ack.MaxPayloadLength).Should().BeTrue();
        ack.IsValidPayloadLength(ack.MinPayloadLength - 1).Should().BeFalse();
        ack.IsValidPayloadLength(ack.MaxPayloadLength + 1).Should().BeFalse();
    }

    [Fact]
    public void All_collection_is_non_empty_and_stable()
    {
        MessageRegistry.All.Count.Should().BeGreaterThan(100,
            "the generated common dialect contains hundreds of messages");
        MessageRegistry.All.Count.Should().Be(MessageRegistry.All.Count);
    }

    private const uint CommandAckId = 77u;
    private const uint SysStatusId = 1u;
}
