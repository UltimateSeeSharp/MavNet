using MavNet.Protocol.Generated;

namespace MavNet.Protocol;

/// <summary>
/// MAVLink v2 wire frame: [0xFD][len][incompat][compat][seq][sysid][compid][msgid(3)][payload][crc(2)].
/// </summary>
public readonly ref struct MavlinkFrame
{
    public byte PayloadLength { get; }
    public byte IncompatFlags { get; }
    public byte CompatFlags { get; }
    public byte Sequence { get; }
    public byte SystemId { get; }
    public byte ComponentId { get; }
    public uint MessageId { get; }
    public ReadOnlySpan<byte> Payload { get; }

    private MavlinkFrame(byte len, byte incompat, byte compat, byte seq,
        byte sysid, byte compid, uint msgid, ReadOnlySpan<byte> payload)
    {
        PayloadLength = len;
        IncompatFlags = incompat;
        CompatFlags = compat;
        Sequence = seq;
        SystemId = sysid;
        ComponentId = compid;
        MessageId = msgid;
        Payload = payload;
    }

    /// <summary>Attempt to decode a MAVLink v2 frame. Returns false if:
    /// <list type="bullet">
    /// <item>data too short / not v2 magic / signed frame (we don't handle signatures yet)</item>
    /// <item>msgid is unknown to the registry — without CrcExtra we can't verify the frame</item>
    /// <item>payload length is outside [MinPayloadLength, MaxPayloadLength] for this msgid</item>
    /// <item>declared payload length doesn't fit in the buffer</item>
    /// <item>CRC doesn't match the per-msgid CRC_EXTRA from MessageRegistry</item>
    /// </list>
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> data, out MavlinkFrame frame)
    {
        frame = default;
        if (data.Length < 12) return false;                          // minimum v2 frame size
        if (data[0] != 0xFD) return false;                            // not v2
        var payloadLen = data[1];
        var incompat = data[2];
        if ((incompat & 0x01) != 0) return false;                    // signed — not yet supported
        // Reject unknown incompat flags per MAVLink spec — receivers MUST drop frames
        // they cannot fully interpret, otherwise forwarding can mangle future protocol features.
        if ((incompat & ~0x01) != 0) return false;
        var totalLen = 10 + payloadLen + 2;
        if (data.Length < totalLen) return false;

        var msgId = (uint)(data[7] | (data[8] << 8) | (data[9] << 16));

        // Registry lookup: we need CrcExtra to validate. Unknown msgid = drop frame
        // rather than guess CrcExtra=0, which would let arbitrary corrupt packets through.
        if (!MessageRegistry.TryGet(msgId, out var info)) return false;

        // Payload length range check (MAVLink 2 truncation may shrink the wire payload,
        // but never below MinPayloadLength nor above MaxPayloadLength).
        if (!info.IsValidPayloadLength(payloadLen)) return false;

        var crcExpected = (ushort)(data[10 + payloadLen] | (data[10 + payloadLen + 1] << 8));
        var crc = Crc16.Accumulate(data[1..(10 + payloadLen)]);
        crc = Crc16.Accumulate(info.CrcExtra, crc);
        if (crc != crcExpected) return false;

        frame = new MavlinkFrame(
            len: payloadLen,
            incompat: incompat,
            compat: data[3],
            seq: data[4],
            sysid: data[5],
            compid: data[6],
            msgid: msgId,
            payload: data.Slice(10, payloadLen));
        return true;
    }
}
