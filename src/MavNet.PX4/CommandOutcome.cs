using MavNet.Protocol.Generated.Enums;

namespace MavNet.PX4;

/// <summary>High-level outcome category for a MAVLink command request.</summary>
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

/// <summary>Outcome of a COMMAND_LONG sent to the vehicle, combining the high-level result,
/// the raw MAV_RESULT ACK code (if received), and the elapsed round-trip time.</summary>
public sealed record CommandOutcome
{
    /// <summary>Overall result category — see <see cref="CommandResult"/>.</summary>
    public CommandResult Result { get; init; }

    /// <summary>MAV_RESULT code from COMMAND_ACK, or <c>null</c> if the command timed out before an ACK arrived.</summary>
    public MavResult? AckResult { get; init; }

    /// <summary>Wall-clock duration from send to terminal outcome.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>Creates a new <see cref="CommandOutcome"/>.</summary>
    public CommandOutcome(CommandResult result, MavResult? ackResult = null, TimeSpan elapsed = default)
    {
        Result = result;
        AckResult = ackResult;
        Elapsed = elapsed;
    }
}
