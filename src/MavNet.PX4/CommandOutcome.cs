using MavNet.Protocol.Generated.Enums;

namespace MavNet.PX4;

public enum CommandResult
{
    /// <summary>Vehicle state changed to the requested value (e.g. armed bit flipped). Strongest signal.</summary>
    Confirmed,
    /// <summary>Vehicle ACK'd with ACCEPTED but state change not yet observed. The command is in flight at PX4.</summary>
    Sent,
    /// <summary>Vehicle ACK'd with a non-ACCEPTED MAV_RESULT (DENIED, FAILED, UNSUPPORTED, etc.).</summary>
    Rejected,
    /// <summary>Neither ACK nor state change observed within the timeout window. Caller may re-poll vehicle state.</summary>
    Timeout,
}

public sealed record CommandOutcome(
    CommandResult Result,
    MavResult? AckResult = null,
    TimeSpan Elapsed = default);
