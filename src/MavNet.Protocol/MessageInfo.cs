namespace MavNet.Protocol;

/// <summary>
/// Per-message metadata required by the MAVLink wire protocol. Returned from
/// <c>MessageRegistry.TryGet</c>; used by the frame parser for CRC validation,
/// by encoders for truncation, and by inspection/replay tooling.
/// </summary>
/// <param name="MsgId">24-bit MAVLink message id (0..16,777,215).</param>
/// <param name="CrcExtra">Per-message seed mixed into the frame CRC. Required to validate inbound CRCs.</param>
/// <param name="MinPayloadLength">Truncation floor — MAVLink v2 senders may strip trailing zero bytes of
/// extension fields down to this length. Wire payload shorter than this is malformed.</param>
/// <param name="MaxPayloadLength">Untruncated payload length. Decoders zero-fill received payloads up to
/// this length before deserialising. Wire payload longer than this is malformed.</param>
/// <param name="Name">SHOUTY_SNAKE_CASE message name from the dialect XML (e.g. "HEARTBEAT").</param>
/// <param name="Flags">Capability hints derived from the spec (HasExtensions, etc.).</param>
public readonly record struct MessageInfo(
    uint MsgId,
    byte CrcExtra,
    byte MinPayloadLength,
    byte MaxPayloadLength,
    string Name,
    MessageFlags Flags = MessageFlags.None)
{
    /// <summary>Wire-length range check. Used by the parser before CRC validation.</summary>
    public bool IsValidPayloadLength(int length) =>
        (uint)length >= MinPayloadLength && (uint)length <= MaxPayloadLength;
}

/// <summary>Capability hints sourced from the dialect XML. Cheap to extend without breaking ABI.</summary>
[System.Flags]
public enum MessageFlags : byte
{
    None          = 0,
    /// <summary>Message has MAVLink 2 extension fields — payload may be truncated on the wire.</summary>
    HasExtensions = 1 << 0,
}
