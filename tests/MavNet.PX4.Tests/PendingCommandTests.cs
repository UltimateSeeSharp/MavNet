using System.Reflection;
using FluentAssertions;
using MavNet.PX4;
using MavNet.Protocol.Generated.Enums;
using Xunit;

namespace MavNet.PX4.Tests;

/// <summary>
/// <see cref="PendingCommand"/> is internal — reach it via reflection on the MavNet.PX4
/// assembly. The class is small but its semantics (one-shot TryComplete, mutable AckAccepted
/// flag) are load-bearing for the command-correlation contract.
/// </summary>
public class PendingCommandTests
{
    private static (object Pending, MethodInfo TryComplete) MakePending(MavCmd cmd)
    {
        var asm = typeof(MavNet.PX4.Base.Vehicle).Assembly;
        var t = asm.GetType("MavNet.PX4.PendingCommand")!;
        var ctor = t.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Single();
        // ctor(MavCmd, Func<Heartbeat,bool>?)
        var instance = ctor.Invoke(new object?[] { cmd, null });
        var tryComplete = t.GetMethod("TryComplete")!;
        return (instance, tryComplete);
    }

    private static bool InvokeTryComplete(object pending, MethodInfo m, CommandResult result, MavResult? ack)
    {
        return (bool)m.Invoke(pending, new object?[] { result, ack })!;
    }

    [Fact]
    public void TryComplete_succeeds_first_time_only()
    {
        var (p, m) = MakePending((MavCmd)400);
        InvokeTryComplete(p, m, CommandResult.Confirmed, MavResult.Accepted).Should().BeTrue();
        InvokeTryComplete(p, m, CommandResult.Timeout, null).Should().BeFalse();
    }

    [Fact]
    public void AckAccepted_round_trips_through_property()
    {
        var (p, _) = MakePending((MavCmd)400);
        var t = p.GetType();
        var prop = t.GetProperty("AckAccepted")!;
        prop.GetValue(p).Should().Be(false);
        prop.SetValue(p, true);
        prop.GetValue(p).Should().Be(true);
    }
}
