namespace MavNet.Protocol;

/// <summary>
/// Contract implemented by every generated MAVLink message type. Lets senders
/// encode and frame the message without runtime dictionary lookups (the protocol
/// metadata is known at the call site through static abstracts).
///
/// <para>Implementations are <c>readonly record struct</c>s emitted by
/// <c>tools/MavNet.CodeGen</c>. Library users normally never write one by hand —
/// they're produced from a dialect XML.</para>
/// </summary>
/// <typeparam name="TSelf">The implementing message type itself (CRTP).</typeparam>
public interface IMavlinkMessage<TSelf> where TSelf : IMavlinkMessage<TSelf>
{
    /// <summary>24-bit MAVLink message id (e.g. 0 for HEARTBEAT).</summary>
    static abstract uint MsgId { get; }

    /// <summary>Per-message CRC_EXTRA seed that MAVLink mixes into the frame CRC.</summary>
    static abstract byte CrcExtra { get; }

    /// <summary>Truncation floor — MAVLink v2 senders may strip trailing zero
    /// bytes of extension fields down to this length. Inbound payloads shorter
    /// than this are malformed.</summary>
    static abstract byte MinPayloadLength { get; }

    /// <summary>Untruncated payload length. Decoders zero-fill inbound payloads
    /// up to this length before deserialising; senders write this many bytes
    /// before truncating.</summary>
    static abstract byte MaxPayloadLength { get; }

    /// <summary>Serialise this message into <paramref name="payload"/>. The span
    /// must be at least <see cref="MaxPayloadLength"/> bytes — the implementation
    /// writes the full untruncated layout. Senders are free to strip trailing
    /// zero bytes down to <see cref="MinPayloadLength"/> for on-wire truncation.</summary>
    void Encode(System.Span<byte> payload);

    /// <summary>Deserialise from <paramref name="payload"/>. Truncated payloads
    /// (length &lt; <see cref="MaxPayloadLength"/> but ≥ <see cref="MinPayloadLength"/>)
    /// are zero-padded automatically. Payloads outside that range must be rejected
    /// by the caller before invoking Decode.</summary>
    static abstract TSelf Decode(System.ReadOnlySpan<byte> payload);
}
