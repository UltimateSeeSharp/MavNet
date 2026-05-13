namespace MavNet.Protocol;

/// <summary>
/// CRC-16/MCRF4XX — the checksum MAVLink uses on every frame. The MAVLink community
/// commonly calls this "CRC-16/X.25", but the reference C implementation omits the
/// X.25 final XOR (xorout=0x0000), which is formally CRC-16/MCRF4XX
/// (poly=0x1021, init=0xFFFF, refin/refout=true, check=0x6F91). Match the reference
/// implementation byte-for-byte so PX4 accepts our outbound frames. Public because
/// the code generator (tools/MavNet.CodeGen) reuses it for CRC_EXTRA computation.
/// </summary>
public static class Crc16
{
    /// <summary>Accumulates a single byte into a running CRC-16/X.25 checksum.</summary>
    public static ushort Accumulate(byte b, ushort crc)
    {
        byte tmp = (byte)(b ^ (crc & 0xff));
        tmp = (byte)(tmp ^ (tmp << 4));
        return (ushort)((crc >> 8) ^ (tmp << 8) ^ (tmp << 3) ^ (tmp >> 4));
    }

    /// <summary>Accumulates a span of bytes into a CRC-16/X.25 checksum.</summary>
    public static ushort Accumulate(ReadOnlySpan<byte> data, ushort crc = 0xFFFF)
    {
        foreach (var b in data) crc = Accumulate(b, crc);
        return crc;
    }
}
