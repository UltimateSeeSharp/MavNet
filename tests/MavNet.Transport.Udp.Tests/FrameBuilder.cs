using MavNet.Protocol;

namespace MavNet.Transport.Udp.Tests;

/// <summary>Builds well-formed MAVLink v2 frames for dispatch tests (CRC pre-computed).</summary>
internal static class FrameBuilder
{
    public static byte[] BuildFrame(
        uint msgId,
        byte crcExtra,
        byte[] payload,
        byte seq = 0,
        byte sysid = 1,
        byte compid = 1,
        byte incompat = 0,
        byte compat = 0)
    {
        var frame = new byte[12 + payload.Length];
        frame[0] = 0xFD;
        frame[1] = (byte)payload.Length;
        frame[2] = incompat;
        frame[3] = compat;
        frame[4] = seq;
        frame[5] = sysid;
        frame[6] = compid;
        frame[7] = (byte)(msgId & 0xFF);
        frame[8] = (byte)((msgId >> 8) & 0xFF);
        frame[9] = (byte)((msgId >> 16) & 0xFF);
        payload.CopyTo(frame, 10);

        var crc = Crc16.Accumulate(frame.AsSpan(1, 9 + payload.Length));
        crc = Crc16.Accumulate(crcExtra, crc);
        frame[10 + payload.Length] = (byte)(crc & 0xFF);
        frame[11 + payload.Length] = (byte)((crc >> 8) & 0xFF);
        return frame;
    }
}
