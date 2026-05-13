using MavNet.Protocol;

namespace MavNet.Protocol.Tests;

/// <summary>
/// Builds well-formed MAVLink v2 frames so each <see cref="MavlinkFrame.TryDecode"/>
/// drop branch can be exercised by corrupting exactly one byte / flag at a time.
/// </summary>
internal static class FrameBuilder
{
    /// <summary>
    /// HEARTBEAT (msgid=0, crc_extra=50, payload=9 bytes). Returns a byte array
    /// of length 12+9 = 21. Caller may mutate before feeding to TryDecode.
    /// </summary>
    public static byte[] BuildHeartbeat(
        byte seq = 0,
        byte sysid = 1,
        byte compid = 1,
        byte incompat = 0,
        byte compat = 0,
        byte[]? payload = null)
    {
        payload ??= new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09 };
        if (payload.Length != 9) throw new System.ArgumentException("HEARTBEAT payload must be 9 bytes.");
        return BuildFrame(
            msgId: 0,
            crcExtra: 50,
            payload: payload,
            seq: seq,
            sysid: sysid,
            compid: compid,
            incompat: incompat,
            compat: compat);
    }

    /// <summary>Builds an arbitrary v2 frame with a freshly computed CRC.</summary>
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

        // CRC over header (skip magic) + payload + crc_extra.
        var crc = Crc16.Accumulate(frame.AsSpan(1, 9 + payload.Length));
        crc = Crc16.Accumulate(crcExtra, crc);
        frame[10 + payload.Length] = (byte)(crc & 0xFF);
        frame[11 + payload.Length] = (byte)((crc >> 8) & 0xFF);
        return frame;
    }
}
