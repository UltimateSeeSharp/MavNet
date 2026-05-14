using FluentAssertions;
using MavNet.Core;
using MavNet.PX4;
using MavNet.Protocol.Generated.Enums;
using MavNet.Protocol.Generated.Messages;
using Xunit;

namespace MavNet.PX4.Tests;

/// <summary>
/// Vehicle.ExecuteCommandAsync resolves through a three-way race: COMMAND_ACK,
/// a state-change HEARTBEAT, and a timeout. These tests cover each path explicitly
/// — the contract is asymmetric (Accepted needs both an ACK *and* a state change to
/// reach Confirmed; otherwise it lands on Sent at the timeout) and easy to break.
/// All tests use a 200 ms timeout (see <see cref="TestDrone"/>) so the full file
/// completes in well under a second.
/// </summary>
public class VehicleCommandCorrelationTests
{
    private static readonly MavId Vehicle11 = new(1, 1);

    private static (TestDrone Drone, FakeMavlinkConnection Conn) Build(TimeSpan? cmdTimeout = null)
    {
        var conn = new FakeMavlinkConnection();
        var drone = new TestDrone(conn, commandTimeout: cmdTimeout);
        return (drone, conn);
    }

    private static Heartbeat Hb(bool armed) => new(
        CustomMode: 0, Type: MavType.Quadrotor, Autopilot: MavAutopilot.Px4,
        BaseMode: armed ? MavModeFlag.SafetyArmed : 0,
        SystemStatus: MavState.Active, MavlinkVersion: 3);

    [Fact]
    public async Task Send_emits_COMMAND_LONG_with_correct_target()
    {
        var (drone, conn) = Build();
        // Fire and immediately abandon — we just want to inspect what got sent.
        _ = drone.ArmAsync();
        await Task.Delay(50);

        conn.SentMessages.Should().HaveCount(1);
        var sent = (CommandLong)conn.SentMessages[0];
        sent.Command.Should().Be(MavCmd.ComponentArmDisarm);
        sent.TargetSystem.Should().Be((byte)1);
        sent.TargetComponent.Should().Be((byte)1);
        sent.Param1.Should().Be(1f, "ArmAsync sets p1=1 to request arming");
    }

    [Fact]
    public async Task Ack_accepted_plus_state_change_resolves_Confirmed()
    {
        var (drone, conn) = Build();
        var task = drone.ArmAsync();

        conn.RaiseCommandAck(Vehicle11, new CommandAck(MavCmd.ComponentArmDisarm, MavResult.Accepted, 0, 0, 0, 0));
        conn.RaiseHeartbeat(Vehicle11, Hb(armed: true));

        var outcome = await task;
        outcome.Result.Should().Be(CommandResult.Confirmed);
        outcome.AckResult.Should().Be(MavResult.Accepted);
    }

    [Fact]
    public async Task Ack_accepted_without_state_change_resolves_Sent_after_timeout()
    {
        var (drone, conn) = Build(cmdTimeout: TimeSpan.FromMilliseconds(150));
        var task = drone.ArmAsync();

        conn.RaiseCommandAck(Vehicle11, new CommandAck(MavCmd.ComponentArmDisarm, MavResult.Accepted, 0, 0, 0, 0));
        // No state-change heartbeat — let the timeout decide.

        var outcome = await task;
        outcome.Result.Should().Be(CommandResult.Sent,
            "ACK Accepted without a state change indicates the vehicle received the command but never confirmed via heartbeat");
        outcome.AckResult.Should().Be(MavResult.Accepted);
    }

    [Theory]
    [InlineData((byte)MavResult.Denied)]
    [InlineData((byte)MavResult.Failed)]
    [InlineData((byte)MavResult.Unsupported)]
    [InlineData((byte)MavResult.TemporarilyRejected)]
    public async Task Ack_rejection_resolves_Rejected(byte resultByte)
    {
        var (drone, conn) = Build();
        var task = drone.ArmAsync();

        conn.RaiseCommandAck(Vehicle11, new CommandAck(MavCmd.ComponentArmDisarm, (MavResult)resultByte, 0, 0, 0, 0));

        var outcome = await task;
        outcome.Result.Should().Be(CommandResult.Rejected);
        outcome.AckResult.Should().Be((MavResult)resultByte);
    }

    [Fact]
    public async Task Ack_InProgress_does_not_resolve_immediately()
    {
        // MAV_RESULT_IN_PROGRESS is the "still working on it" reply — the command must
        // stay pending until a subsequent ACK (or state change / timeout) arrives.
        var (drone, conn) = Build(cmdTimeout: TimeSpan.FromMilliseconds(200));
        var task = drone.ArmAsync();

        conn.RaiseCommandAck(Vehicle11, new CommandAck(MavCmd.ComponentArmDisarm, MavResult.InProgress, 50, 0, 0, 0));
        await Task.Delay(50);
        task.IsCompleted.Should().BeFalse();

        conn.RaiseCommandAck(Vehicle11, new CommandAck(MavCmd.ComponentArmDisarm, MavResult.Accepted, 100, 0, 0, 0));
        conn.RaiseHeartbeat(Vehicle11, Hb(armed: true));

        (await task).Result.Should().Be(CommandResult.Confirmed);
    }

    [Fact]
    public async Task No_ack_at_all_resolves_Timeout()
    {
        var (drone, _) = Build(cmdTimeout: TimeSpan.FromMilliseconds(100));
        var outcome = await drone.ArmAsync();
        outcome.Result.Should().Be(CommandResult.Timeout);
        outcome.AckResult.Should().BeNull();
    }

    [Fact]
    public async Task Ack_for_different_command_id_is_ignored()
    {
        var (drone, conn) = Build(cmdTimeout: TimeSpan.FromMilliseconds(100));
        var task = drone.ArmAsync();

        // ACK for a different command (NavTakeoff) — must not resolve our Arm request.
        conn.RaiseCommandAck(Vehicle11, new CommandAck(MavCmd.NavTakeoff, MavResult.Accepted, 0, 0, 0, 0));

        var outcome = await task;
        outcome.Result.Should().Be(CommandResult.Timeout, "the ACK matched a different command id");
    }

    [Fact]
    public async Task Ack_from_different_sysid_is_ignored()
    {
        var (drone, conn) = Build(cmdTimeout: TimeSpan.FromMilliseconds(100));
        var task = drone.ArmAsync();

        conn.RaiseCommandAck(new MavId(99, 1),
            new CommandAck(MavCmd.ComponentArmDisarm, MavResult.Accepted, 0, 0, 0, 0));

        var outcome = await task;
        outcome.Result.Should().Be(CommandResult.Timeout, "the ACK came from a different vehicle");
    }

    [Fact]
    public async Task Cancellation_resolves_Timeout()
    {
        var (drone, _) = Build(cmdTimeout: TimeSpan.FromSeconds(10));
        using var cts = new CancellationTokenSource();

        var task = drone.ArmAsync(cts.Token);
        cts.Cancel();

        var outcome = await task;
        outcome.Result.Should().Be(CommandResult.Timeout);
    }

    [Fact]
    public async Task StartMissionAsync_sends_MISSION_START_and_confirms_on_AUTO_MISSION_heartbeat()
    {
        var (drone, conn) = Build();
        var task = drone.StartMissionAsync();

        conn.RaiseCommandAck(Vehicle11, new CommandAck(MavCmd.MissionStart, MavResult.Accepted, 0, 0, 0, 0));
        // PX4 main-mode 4, sub-mode 4 = AUTO.MISSION
        conn.RaiseHeartbeat(Vehicle11, new Heartbeat(
            CustomMode: (4u << 24) | (4u << 16),
            Type: MavType.Quadrotor, Autopilot: MavAutopilot.Px4,
            BaseMode: MavModeFlag.SafetyArmed | MavModeFlag.CustomModeEnabled,
            SystemStatus: MavState.Active, MavlinkVersion: 3));

        var outcome = await task;
        outcome.Result.Should().Be(CommandResult.Confirmed);
        var sent = (CommandLong)conn.SentMessages[0];
        sent.Command.Should().Be(MavCmd.MissionStart);
        sent.Param1.Should().Be(0f, "default firstItem = 0");
        sent.Param2.Should().Be(-1f, "default lastItem = -1 means 'to end'");
    }

    [Fact]
    public void StartMissionAsync_rejects_out_of_range_arguments()
    {
        var (drone, _) = Build();
        Action negFirst = () => { _ = drone.StartMissionAsync(firstItem: -1); };
        negFirst.Should().Throw<ArgumentOutOfRangeException>();

        Action badLast = () => { _ = drone.StartMissionAsync(lastItem: -2); };
        badLast.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task SetCurrentMissionItem_sends_COMMAND_LONG_with_DO_SET_MISSION_CURRENT_and_seq()
    {
        var (drone, conn) = Build();
        var task = drone.SetCurrentMissionItemAsync(seq: 2);

        conn.RaiseCommandAck(Vehicle11, new CommandAck(MavCmd.DoSetMissionCurrent, MavResult.Accepted, 0, 0, 0, 0));
        var outcome = await task;

        conn.SentMessages.Should().HaveCount(1);
        var sent = (CommandLong)conn.SentMessages[0];
        sent.Command.Should().Be(MavCmd.DoSetMissionCurrent);
        sent.Param1.Should().Be(2f, "seq is encoded as param1");
        // SetCurrentMissionItemAsync passes no state matcher to ExecuteCommandAsync, so an
        // Accepted ACK without a subsequent state change resolves as Sent once the timeout fires.
        outcome.AckResult.Should().Be(MavResult.Accepted);
    }

    [Fact]
    public void SetCurrentMissionItem_rejects_out_of_range_seq()
    {
        var (drone, _) = Build();
        Action negative = () => { _ = drone.SetCurrentMissionItemAsync(seq: -1); };
        negative.Should().Throw<ArgumentOutOfRangeException>();

        Action tooBig = () => { _ = drone.SetCurrentMissionItemAsync(seq: 70000); };
        tooBig.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Issuing_a_second_command_cancels_the_first()
    {
        var (drone, conn) = Build(cmdTimeout: TimeSpan.FromMilliseconds(200));

        var first = drone.ArmAsync();
        var second = drone.DisarmAsync();

        // The first should resolve as Timeout (cancelled by second).
        var firstOutcome = await first;
        firstOutcome.Result.Should().Be(CommandResult.Timeout,
            "ExecuteCommandAsync swaps the pending command atomically and completes the prior one");

        // Resolve the second so the test finishes cleanly.
        conn.RaiseCommandAck(Vehicle11, new CommandAck(MavCmd.ComponentArmDisarm, MavResult.Accepted, 0, 0, 0, 0));
        conn.RaiseHeartbeat(Vehicle11, Hb(armed: false));
        (await second).Result.Should().Be(CommandResult.Confirmed);
    }
}
