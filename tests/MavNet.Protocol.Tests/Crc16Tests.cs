using FluentAssertions;
using MavNet.Protocol;
using Xunit;

namespace MavNet.Protocol.Tests;

/// <summary>
/// CRC reference vectors. MAVLink calls this "CRC-16/X.25" but the reference C
/// implementation omits the X.25 final XOR — formally this is CRC-16/MCRF4XX
/// (poly=0x1021, init=0xFFFF, refin/refout=true, xorout=0x0000, check=0x6F91).
/// Any drift here will be catastrophic — PX4 silently drops every outbound frame.
/// </summary>
public class Crc16Tests
{
    [Fact]
    public void Empty_buffer_returns_seed()
    {
        Crc16.Accumulate(System.ReadOnlySpan<byte>.Empty).Should().Be((ushort)0xFFFF);
    }

    [Fact]
    public void Standard_check_vector_matches_mavlink_reference()
    {
        // CRC-16/MCRF4XX check value for ASCII "123456789" is 0x6F91. This is the
        // value the MAVLink reference C implementation produces and the value PX4
        // expects on the wire. See https://reveng.sourceforge.io/crc-catalogue/16.htm.
        var data = System.Text.Encoding.ASCII.GetBytes("123456789");
        Crc16.Accumulate(data).Should().Be((ushort)0x6F91);
    }

    [Fact]
    public void Byte_at_a_time_equals_bulk()
    {
        var data = new byte[] { 0x01, 0x02, 0x03, 0xFE, 0xFD, 0x7F, 0x80, 0x00, 0xFF };

        ushort crc = 0xFFFF;
        foreach (var b in data) crc = Crc16.Accumulate(b, crc);

        crc.Should().Be(Crc16.Accumulate(data));
    }

    [Fact]
    public void Default_seed_is_0xFFFF()
    {
        var data = new byte[] { 0xAB, 0xCD, 0xEF };
        Crc16.Accumulate(data).Should().Be(Crc16.Accumulate(data, 0xFFFF));
    }

    [Fact]
    public void Accumulating_extra_byte_is_compositional()
    {
        // Frame CRC is computed as Accumulate(header+payload) then Accumulate(CrcExtra, prior).
        // Verify that "running CRC then add one more byte" matches "CRC of the concatenation".
        var prefix = new byte[] { 0x09, 0x00, 0x00, 0x00, 0x05, 0xFF, 0xBE, 0x00, 0x00, 0x00 };
        const byte extra = 50;
        var combined = new byte[prefix.Length + 1];
        prefix.CopyTo(combined, 0);
        combined[^1] = extra;

        var stepped = Crc16.Accumulate(extra, Crc16.Accumulate(prefix));
        stepped.Should().Be(Crc16.Accumulate(combined));
    }
}
