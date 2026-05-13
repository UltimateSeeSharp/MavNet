namespace MavNet.Protocol;

/// <summary>
/// CRC-16/X.25 (a.k.a. CRC-CCITT) — the checksum MAVLink uses on every frame.
/// Initial value 0xFFFF, no final XOR. Match the reference C implementation
/// byte-for-byte so PX4 accepts our outbound frames. Public because the code
/// generator (tools/MavLinkCodeGen) reuses it for CRC_EXTRA computation.
/// </summary>
public static class Crc16
{
    public static ushort Accumulate(byte b, ushort crc)
    {
        byte tmp = (byte)(b ^ (crc & 0xff));
        tmp = (byte)(tmp ^ (tmp << 4));
        return (ushort)((crc >> 8) ^ (tmp << 8) ^ (tmp << 3) ^ (tmp >> 4));
    }

    public static ushort Accumulate(ReadOnlySpan<byte> data, ushort crc = 0xFFFF)
    {
        foreach (var b in data) crc = Accumulate(b, crc);
        return crc;
    }
}
