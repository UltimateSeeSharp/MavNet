using MavNet.Protocol.Generated.Enums;
using MavNet.Protocol.Generated.Messages;

namespace MavNet.PX4;

/// <summary>
/// Internal bookkeeping for one in-flight COMMAND_LONG awaiting ACK + state change.
/// Whoever finishes first — ACK with Accepted/Rejected, matching state-change heartbeat,
/// or timeout — completes the TaskCompletionSource. Losers are no-ops via TrySetResult.
/// </summary>
internal sealed class PendingCommand
{
    public MavCmd Command { get; }
    public Func<Heartbeat, bool>? StateMatcher { get; }
    public TaskCompletionSource<CommandOutcome> Tcs { get; }
    public DateTime StartedAtUtc { get; }

    /// <summary>Set true when COMMAND_ACK with MAV_RESULT_ACCEPTED arrived. Decides between
    /// <see cref="CommandResult.Sent"/> (ACK seen but no state change) and
    /// <see cref="CommandResult.Timeout"/> (neither) when the timer fires.</summary>
    public bool AckAccepted { get; set; }

    public PendingCommand(MavCmd command, Func<Heartbeat, bool>? stateMatcher)
    {
        Command = command;
        StateMatcher = stateMatcher;
        Tcs = new TaskCompletionSource<CommandOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartedAtUtc = DateTime.UtcNow;
    }

    public bool TryComplete(CommandResult result, MavResult? ack) =>
        Tcs.TrySetResult(new CommandOutcome(result, ack, DateTime.UtcNow - StartedAtUtc));
}
