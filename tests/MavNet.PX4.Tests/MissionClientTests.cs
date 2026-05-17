using FluentAssertions;
using MavNet.Core;
using MavNet.PX4.Missions;
using MavNet.Protocol.Generated.Enums;
using MavNet.Protocol.Generated.Messages;
using Xunit;

namespace MavNet.PX4.Tests;

/// <summary>
/// State-machine tests for <see cref="MissionClient"/> driven against
/// <see cref="FakeMavlinkConnection"/>. Spec timing (1500 ms / 250 ms / 5 retries)
/// is overridden to small values per test so the whole file completes in well
/// under a second. The state machine itself is the same.
/// </summary>
public class MissionClientTests
{
    private static readonly MavId Vehicle11 = new(1, 1);

    private static readonly TimeSpan FastDefault = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan FastItem    = TimeSpan.FromMilliseconds(30);

    private static (MissionClient Client, FakeMavlinkConnection Conn) Build(
        int maxRetries = 5,
        MavMissionType type = MavMissionType.Mission,
        TimeSpan? defaultTimeout = null,
        TimeSpan? itemTimeout = null)
    {
        var conn = new FakeMavlinkConnection();
        var client = new MissionClient(
            conn,
            targetSystem: 1, targetComponent: 1,
            missionType: type,
            defaultTimeout: defaultTimeout ?? FastDefault,
            itemTimeout: itemTimeout ?? FastItem,
            maxRetries: maxRetries);
        return (client, conn);
    }

    private static MissionItem[] ThreeWaypoints() => new[]
    {
        MissionItem.Waypoint(47.0, 8.5, 30f),
        MissionItem.Waypoint(47.001, 8.501, 30f),
        MissionItem.Waypoint(47.002, 8.502, 30f),
    };

    private static MissionAck Ack(MavMissionResult result, uint opaqueId = 0u,
        MavMissionType type = MavMissionType.Mission) =>
        new(TargetSystem: 255, TargetComponent: 190, Type: result, MissionType: type, OpaqueId: opaqueId);

    [Fact]
    public async Task UploadAsync_happy_path_completes_Accepted_with_opaque_id()
    {
        var (client, conn) = Build();
        using var disp = client;

        var task = client.UploadAsync(ThreeWaypoints());

        // Simulate the vehicle pumping requests + final ACK.
        conn.RaiseMissionRequestInt(Vehicle11, new MissionRequestInt(0, 1, 1, MavMissionType.Mission));
        conn.RaiseMissionRequestInt(Vehicle11, new MissionRequestInt(1, 1, 1, MavMissionType.Mission));
        conn.RaiseMissionRequestInt(Vehicle11, new MissionRequestInt(2, 1, 1, MavMissionType.Mission));
        conn.RaiseMissionAck(Vehicle11, Ack(MavMissionResult.MavMissionAccepted, opaqueId: 0xDEADBEEFu));

        var result = await task;

        result.Status.Should().Be(MissionTransactionStatus.Accepted);
        result.AckResult.Should().Be(MavMissionResult.MavMissionAccepted);
        result.OpaqueId.Should().Be(0xDEADBEEFu);
        result.IsAccepted.Should().BeTrue();
    }

    [Fact]
    public async Task UploadAsync_first_send_is_MISSION_COUNT_with_correct_fields()
    {
        var (client, conn) = Build();
        using var disp = client;

        _ = client.UploadAsync(ThreeWaypoints());
        await Task.Delay(20); // let the Send happen

        conn.SentMessages.Should().NotBeEmpty();
        var count = conn.SentMessages.OfType<MissionCount>().Single();
        count.Count.Should().Be((ushort)3);
        count.TargetSystem.Should().Be((byte)1);
        count.TargetComponent.Should().Be((byte)1);
        count.MissionType.Should().Be(MavMissionType.Mission);
    }

    [Fact]
    public async Task UploadAsync_responds_to_MISSION_REQUEST_INT_with_the_requested_item()
    {
        var (client, conn) = Build();
        using var disp = client;

        var task = client.UploadAsync(ThreeWaypoints());
        conn.RaiseMissionRequestInt(Vehicle11, new MissionRequestInt(1, 1, 1, MavMissionType.Mission));
        // Complete so the test doesn't dangle.
        conn.RaiseMissionAck(Vehicle11, Ack(MavMissionResult.MavMissionError));
        await task;

        var items = conn.SentMessages.OfType<MissionItemInt>().ToList();
        items.Should().HaveCount(1, "we only answered the seq=1 request");
        items[0].Seq.Should().Be((ushort)1);
        items[0].TargetSystem.Should().Be((byte)1);
        items[0].MissionType.Should().Be(MavMissionType.Mission);
        items[0].Command.Should().Be(MavCmd.NavWaypoint);
    }

    [Fact]
    public async Task UploadAsync_deprecated_MISSION_REQUEST_is_answered_with_MISSION_ITEM_INT()
    {
        // ArduPilot still emits MISSION_REQUEST (msgid 40). Per spec we respond with
        // MISSION_ITEM_INT identically — verifying the same item content as the INT variant.
        var (client, conn) = Build();
        using var disp = client;

        var task = client.UploadAsync(ThreeWaypoints());
        conn.RaiseMissionRequest(Vehicle11, new MissionRequest(2, 1, 1, MavMissionType.Mission));
        conn.RaiseMissionAck(Vehicle11, Ack(MavMissionResult.MavMissionError));
        await task;

        var items = conn.SentMessages.OfType<MissionItemInt>().ToList();
        items.Should().HaveCount(1);
        items[0].Seq.Should().Be((ushort)2);
    }

    [Fact]
    public async Task UploadAsync_NACK_resolves_Rejected_with_AckResult()
    {
        var (client, conn) = Build();
        using var disp = client;

        var task = client.UploadAsync(ThreeWaypoints());
        conn.RaiseMissionAck(Vehicle11, Ack(MavMissionResult.MavMissionNoSpace));

        var result = await task;

        result.Status.Should().Be(MissionTransactionStatus.Rejected);
        result.AckResult.Should().Be(MavMissionResult.MavMissionNoSpace);
        result.OpaqueId.Should().BeNull("opaque_id is only set on Accepted");
    }

    [Fact]
    public async Task UploadAsync_repeated_seq_request_resends_same_item_not_an_error()
    {
        // Spec: "Out-of-sequence messages in mission upload/download are recoverable,
        // and are not treated as errors." The repeated seq=0 must be re-answered with item 0.
        var (client, conn) = Build();
        using var disp = client;

        var task = client.UploadAsync(ThreeWaypoints());
        conn.RaiseMissionRequestInt(Vehicle11, new MissionRequestInt(0, 1, 1, MavMissionType.Mission));
        conn.RaiseMissionRequestInt(Vehicle11, new MissionRequestInt(0, 1, 1, MavMissionType.Mission));
        conn.RaiseMissionAck(Vehicle11, Ack(MavMissionResult.MavMissionAccepted));

        await task;

        var items = conn.SentMessages.OfType<MissionItemInt>().Where(i => i.Seq == 0).ToList();
        items.Should().HaveCount(2, "the vehicle re-requested seq=0; we must re-answer rather than abort");
    }

    [Fact]
    public async Task UploadAsync_out_of_range_seq_is_ignored_not_treated_as_error()
    {
        // Vehicle requests seq=99 when we only uploaded 3 items. Per spec we treat it
        // as recoverable (just ignore and let the watchdog re-send).
        var (client, conn) = Build(maxRetries: 0); // 0 retries so the watchdog hits Timeout fast
        using var disp = client;

        var task = client.UploadAsync(ThreeWaypoints());
        conn.RaiseMissionRequestInt(Vehicle11, new MissionRequestInt(99, 1, 1, MavMissionType.Mission));

        var result = await task;

        result.Status.Should().Be(MissionTransactionStatus.Timeout,
            "out-of-range request is ignored; the upload eventually times out without an ACK");
        conn.SentMessages.OfType<MissionItemInt>().Should().BeEmpty("we never answered the bogus seq=99");
    }

    [Fact]
    public async Task UploadAsync_no_initial_request_retries_count_then_times_out()
    {
        var (client, conn) = Build(maxRetries: 2,
            defaultTimeout: TimeSpan.FromMilliseconds(30));
        using var disp = client;

        var task = client.UploadAsync(ThreeWaypoints());
        // Vehicle stays silent. Watchdog should resend MISSION_COUNT up to maxRetries times.

        var result = await task;

        result.Status.Should().Be(MissionTransactionStatus.Timeout);
        // initial send + 2 retries = 3 MISSION_COUNTs.
        conn.SentMessages.OfType<MissionCount>().Should().HaveCount(3,
            "initial COUNT plus maxRetries retries");
        conn.SentMessages.OfType<MissionItemInt>().Should().BeEmpty();
    }

    [Fact]
    public async Task UploadAsync_no_item_response_retries_item_then_times_out()
    {
        var (client, conn) = Build(maxRetries: 2);
        using var disp = client;

        var task = client.UploadAsync(ThreeWaypoints());
        // Vehicle requests seq=0 then goes silent.
        conn.RaiseMissionRequestInt(Vehicle11, new MissionRequestInt(0, 1, 1, MavMissionType.Mission));

        var result = await task;

        result.Status.Should().Be(MissionTransactionStatus.Timeout);
        // initial item send + 2 retries = 3 ITEM_INTs for seq=0.
        var seq0Items = conn.SentMessages.OfType<MissionItemInt>().Where(i => i.Seq == 0).ToList();
        seq0Items.Should().HaveCount(3,
            "initial item answer plus maxRetries retries of the same seq");
    }

    [Fact]
    public async Task UploadAsync_cancellation_resolves_Cancelled()
    {
        var (client, conn) = Build(
            defaultTimeout: TimeSpan.FromSeconds(10),  // long, so cancellation wins
            itemTimeout: TimeSpan.FromSeconds(10),
            maxRetries: 5);
        using var disp = client;
        using var cts = new CancellationTokenSource();

        var task = client.UploadAsync(ThreeWaypoints(), cts.Token);
        cts.Cancel();

        var result = await task;
        result.Status.Should().Be(MissionTransactionStatus.Cancelled);
    }

    [Fact]
    public async Task UploadAsync_second_concurrent_call_throws_InvalidOperationException()
    {
        var (client, conn) = Build(
            defaultTimeout: TimeSpan.FromSeconds(10),
            itemTimeout: TimeSpan.FromSeconds(10));
        using var disp = client;

        var first = client.UploadAsync(ThreeWaypoints());

        Action act = () => { _ = client.UploadAsync(ThreeWaypoints()); };
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*already in progress*");

        // Let the first complete cleanly so the test doesn't dangle.
        conn.RaiseMissionAck(Vehicle11, Ack(MavMissionResult.MavMissionAccepted));
        await first;
    }

    [Fact]
    public async Task UploadAsync_after_one_completes_second_can_run()
    {
        var (client, conn) = Build();
        using var disp = client;

        var first = client.UploadAsync(ThreeWaypoints());
        conn.RaiseMissionAck(Vehicle11, Ack(MavMissionResult.MavMissionAccepted));
        (await first).Status.Should().Be(MissionTransactionStatus.Accepted);

        var second = client.UploadAsync(ThreeWaypoints());
        conn.RaiseMissionAck(Vehicle11, Ack(MavMissionResult.MavMissionAccepted));
        (await second).Status.Should().Be(MissionTransactionStatus.Accepted);
    }

    [Fact]
    public async Task UploadAsync_empty_mission_sends_count_zero_and_awaits_ack()
    {
        var (client, conn) = Build();
        using var disp = client;

        var task = client.UploadAsync(System.Array.Empty<MissionItem>());
        // Vehicle treats COUNT=0 as a clear and ACKs directly without requesting any item.
        conn.RaiseMissionAck(Vehicle11, Ack(MavMissionResult.MavMissionAccepted, opaqueId: 0u));

        var result = await task;

        result.Status.Should().Be(MissionTransactionStatus.Accepted);
        conn.SentMessages.OfType<MissionCount>().Single().Count.Should().Be((ushort)0);
        conn.SentMessages.OfType<MissionItemInt>().Should().BeEmpty();
    }

    [Fact]
    public async Task UploadAsync_request_from_wrong_sysid_is_ignored()
    {
        var (client, conn) = Build(maxRetries: 0,
            defaultTimeout: TimeSpan.FromMilliseconds(50));
        using var disp = client;

        var task = client.UploadAsync(ThreeWaypoints());
        // Request from a different vehicle (sysid 99) — must not be answered.
        conn.RaiseMissionRequestInt(new MavId(99, 1), new MissionRequestInt(0, 1, 1, MavMissionType.Mission));

        var result = await task;
        result.Status.Should().Be(MissionTransactionStatus.Timeout);
        conn.SentMessages.OfType<MissionItemInt>().Should().BeEmpty("the request came from a different vehicle");
    }

    [Fact]
    public async Task UploadAsync_request_with_wrong_mission_type_is_ignored()
    {
        // Client manages MAV_MISSION_TYPE_MISSION. A request for FENCE belongs to a
        // different client — must not be answered.
        var (client, conn) = Build(maxRetries: 0,
            defaultTimeout: TimeSpan.FromMilliseconds(50),
            type: MavMissionType.Mission);
        using var disp = client;

        var task = client.UploadAsync(ThreeWaypoints());
        conn.RaiseMissionRequestInt(Vehicle11, new MissionRequestInt(0, 1, 1, MavMissionType.Fence));

        var result = await task;
        result.Status.Should().Be(MissionTransactionStatus.Timeout);
        conn.SentMessages.OfType<MissionItemInt>().Should().BeEmpty(
            "the request was for a different MAV_MISSION_TYPE");
    }

    [Fact]
    public async Task UploadAsync_ack_for_wrong_mission_type_is_ignored()
    {
        var (client, conn) = Build(maxRetries: 0,
            defaultTimeout: TimeSpan.FromMilliseconds(50));
        using var disp = client;

        var task = client.UploadAsync(ThreeWaypoints());
        // ACK for FENCE — not ours, must not resolve our transaction.
        conn.RaiseMissionAck(Vehicle11, Ack(MavMissionResult.MavMissionAccepted, type: MavMissionType.Fence));

        var result = await task;
        result.Status.Should().Be(MissionTransactionStatus.Timeout,
            "the ACK was for a different MAV_MISSION_TYPE; our upload eventually times out");
    }

    [Fact]
    public async Task Dispose_during_upload_resolves_Cancelled()
    {
        var (client, conn) = Build(
            defaultTimeout: TimeSpan.FromSeconds(10),
            itemTimeout: TimeSpan.FromSeconds(10));

        var task = client.UploadAsync(ThreeWaypoints());
        client.Dispose();

        var result = await task;
        result.Status.Should().Be(MissionTransactionStatus.Cancelled);
    }

    [Fact]
    public void UploadAsync_after_dispose_throws_ObjectDisposedException()
    {
        var (client, _) = Build();
        client.Dispose();

        Action act = () => { _ = client.UploadAsync(ThreeWaypoints()); };
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Constructor_negative_maxRetries_throws()
    {
        var conn = new FakeMavlinkConnection();
        var act = () => new MissionClient(conn, 1, 1, maxRetries: -1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Defaults_match_MAVLink_spec()
    {
        // 1500 ms / 250 ms / 5 retries per the spec. Pin them so a future tweak shows up in review.
        var conn = new FakeMavlinkConnection();
        using var client = new MissionClient(conn, 1, 1);

        client.DefaultTimeout.Should().Be(TimeSpan.FromMilliseconds(1500));
        client.ItemTimeout.Should().Be(TimeSpan.FromMilliseconds(250));
        client.MaxRetries.Should().Be(5);
        client.MissionType.Should().Be(MavMissionType.Mission);
    }

    // ===== Download =========================================================

    private static MissionCount Count(ushort count, uint opaqueId = 0u,
        MavMissionType type = MavMissionType.Mission) =>
        new(Count: count, TargetSystem: 255, TargetComponent: 190, MissionType: type, OpaqueId: opaqueId);

    private static MissionItemInt WireItem(ushort seq, MavCmd command = MavCmd.NavWaypoint,
        int x = 0, int y = 0, float z = 0f,
        MavMissionType type = MavMissionType.Mission) =>
        new(Param1: 0f, Param2: 0f, Param3: 0f, Param4: 0f,
            X: x, Y: y, Z: z,
            Seq: seq, Command: command,
            TargetSystem: 255, TargetComponent: 190,
            Frame: MavFrame.GlobalRelativeAltInt,
            Current: 0, Autocontinue: 1,
            MissionType: type);

    [Fact]
    public async Task DownloadAsync_first_send_is_MISSION_REQUEST_LIST_with_correct_target_and_type()
    {
        var (client, conn) = Build();
        using var disp = client;

        _ = client.DownloadAsync();
        await Task.Delay(20);

        var rq = conn.SentMessages.OfType<MissionRequestList>().Single();
        rq.TargetSystem.Should().Be((byte)1);
        rq.TargetComponent.Should().Be((byte)1);
        rq.MissionType.Should().Be(MavMissionType.Mission);
    }

    [Fact]
    public async Task DownloadAsync_happy_path_collects_items_and_sends_final_ACK()
    {
        var (client, conn) = Build();
        using var disp = client;

        var task = client.DownloadAsync();
        conn.RaiseMissionCount(Vehicle11, Count(3, opaqueId: 0xDEADBEEFu));
        conn.RaiseMissionItemInt(Vehicle11, WireItem(0, x: 1_000_0000, y: 2_000_0000, z: 10f));
        conn.RaiseMissionItemInt(Vehicle11, WireItem(1, x: 1_100_0000, y: 2_100_0000, z: 20f));
        conn.RaiseMissionItemInt(Vehicle11, WireItem(2, x: 1_200_0000, y: 2_200_0000, z: 30f));

        var result = await task;

        result.Status.Should().Be(MissionTransactionStatus.Accepted);
        result.IsAccepted.Should().BeTrue();
        result.OpaqueId.Should().Be(0xDEADBEEFu);
        result.Items.Should().HaveCount(3);
        result.Items[0].X.Should().Be(1_000_0000);
        result.Items[2].Z.Should().Be(30f);

        // GCS must send MISSION_ACK(Accepted) after the final item.
        var finalAck = conn.SentMessages.OfType<MissionAck>().Single();
        finalAck.Type.Should().Be(MavMissionResult.MavMissionAccepted);
        finalAck.MissionType.Should().Be(MavMissionType.Mission);
        finalAck.TargetSystem.Should().Be((byte)1);

        // Expected REQUEST_INT count = 3 (one per item).
        conn.SentMessages.OfType<MissionRequestInt>().Should().HaveCount(3);
    }

    [Fact]
    public async Task DownloadAsync_empty_mission_completes_immediately_after_ACK()
    {
        var (client, conn) = Build();
        using var disp = client;

        var task = client.DownloadAsync();
        conn.RaiseMissionCount(Vehicle11, Count(0, opaqueId: 0u));

        var result = await task;
        result.Status.Should().Be(MissionTransactionStatus.Accepted);
        result.Items.Should().BeEmpty();
        // No item requests are sent for an empty mission, but we still close with ACK.
        conn.SentMessages.OfType<MissionRequestInt>().Should().BeEmpty();
        conn.SentMessages.OfType<MissionAck>().Single().Type.Should().Be(MavMissionResult.MavMissionAccepted);
    }

    [Fact]
    public async Task DownloadAsync_no_COUNT_response_retries_then_times_out()
    {
        var (client, conn) = Build(maxRetries: 2,
            defaultTimeout: TimeSpan.FromMilliseconds(30));
        using var disp = client;

        var task = client.DownloadAsync();
        // Vehicle stays silent — no MISSION_COUNT ever.

        var result = await task;

        result.Status.Should().Be(MissionTransactionStatus.Timeout);
        // initial REQUEST_LIST + 2 retries.
        conn.SentMessages.OfType<MissionRequestList>().Should().HaveCount(3);
    }

    [Fact]
    public async Task DownloadAsync_no_ITEM_response_retries_then_times_out()
    {
        var (client, conn) = Build(maxRetries: 2);
        using var disp = client;

        var task = client.DownloadAsync();
        conn.RaiseMissionCount(Vehicle11, Count(3));
        // No items arrive — vehicle ghosts us after the COUNT.

        var result = await task;

        result.Status.Should().Be(MissionTransactionStatus.Timeout);
        var seq0Requests = conn.SentMessages.OfType<MissionRequestInt>().Where(r => r.Seq == 0).ToList();
        seq0Requests.Should().HaveCount(3, "initial REQUEST_INT(0) plus maxRetries retries");
    }

    [Fact]
    public async Task DownloadAsync_out_of_sequence_item_is_ignored_and_re_requested()
    {
        // Vehicle sends ITEM_INT(2) when we asked for seq=0. Per spec out-of-sequence
        // is recoverable — we ignore and the watchdog re-requests seq=0.
        var (client, conn) = Build(maxRetries: 2);
        using var disp = client;

        var task = client.DownloadAsync();
        conn.RaiseMissionCount(Vehicle11, Count(3));
        conn.RaiseMissionItemInt(Vehicle11, WireItem(2)); // wrong seq

        var result = await task;
        result.Status.Should().Be(MissionTransactionStatus.Timeout,
            "the bogus item is ignored, watchdog retries seq=0, vehicle still silent → Timeout");
    }

    [Fact]
    public async Task DownloadAsync_cancellation_resolves_Cancelled()
    {
        var (client, conn) = Build(
            defaultTimeout: TimeSpan.FromSeconds(10),
            itemTimeout: TimeSpan.FromSeconds(10));
        using var disp = client;
        using var cts = new CancellationTokenSource();

        var task = client.DownloadAsync(cts.Token);
        cts.Cancel();

        (await task).Status.Should().Be(MissionTransactionStatus.Cancelled);
    }

    [Fact]
    public async Task DownloadAsync_concurrent_with_Upload_throws_InvalidOperationException()
    {
        // Single-flight gate spans both directions per the spec ("one transaction at a time per type").
        var (client, conn) = Build(
            defaultTimeout: TimeSpan.FromSeconds(10),
            itemTimeout: TimeSpan.FromSeconds(10));
        using var disp = client;

        var upload = client.UploadAsync(ThreeWaypoints());

        Action act = () => { _ = client.DownloadAsync(); };
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*already in progress*");

        // Let the upload complete so the test doesn't dangle.
        conn.RaiseMissionAck(Vehicle11, Ack(MavMissionResult.MavMissionAccepted));
        await upload;
    }

    [Fact]
    public async Task DownloadAsync_count_from_wrong_sysid_is_ignored()
    {
        var (client, conn) = Build(maxRetries: 0,
            defaultTimeout: TimeSpan.FromMilliseconds(50));
        using var disp = client;

        var task = client.DownloadAsync();
        conn.RaiseMissionCount(new MavId(99, 1), Count(3));

        (await task).Status.Should().Be(MissionTransactionStatus.Timeout);
        conn.SentMessages.OfType<MissionRequestInt>().Should().BeEmpty();
    }

    [Fact]
    public async Task DownloadAsync_count_for_wrong_mission_type_is_ignored()
    {
        var (client, conn) = Build(maxRetries: 0,
            defaultTimeout: TimeSpan.FromMilliseconds(50));
        using var disp = client;

        var task = client.DownloadAsync();
        conn.RaiseMissionCount(Vehicle11, Count(3, type: MavMissionType.Fence));

        (await task).Status.Should().Be(MissionTransactionStatus.Timeout);
        conn.SentMessages.OfType<MissionRequestInt>().Should().BeEmpty(
            "the COUNT was for a different MAV_MISSION_TYPE");
    }

    [Fact]
    public async Task DownloadAsync_item_for_wrong_mission_type_is_ignored()
    {
        var (client, conn) = Build(maxRetries: 0);
        using var disp = client;

        var task = client.DownloadAsync();
        conn.RaiseMissionCount(Vehicle11, Count(3));
        conn.RaiseMissionItemInt(Vehicle11, WireItem(0, type: MavMissionType.Fence));

        (await task).Status.Should().Be(MissionTransactionStatus.Timeout);
    }

    [Fact]
    public async Task DownloadAsync_after_one_completes_second_can_run()
    {
        var (client, conn) = Build();
        using var disp = client;

        var first = client.DownloadAsync();
        conn.RaiseMissionCount(Vehicle11, Count(0));
        (await first).Status.Should().Be(MissionTransactionStatus.Accepted);

        var second = client.DownloadAsync();
        conn.RaiseMissionCount(Vehicle11, Count(0));
        (await second).Status.Should().Be(MissionTransactionStatus.Accepted);
    }

    // ===== Clear ============================================================

    [Fact]
    public async Task ClearAsync_sends_MISSION_CLEAR_ALL_with_correct_target_and_type()
    {
        var (client, conn) = Build();
        using var disp = client;

        _ = client.ClearAsync();
        await Task.Delay(20);

        var clr = conn.SentMessages.OfType<MissionClearAll>().Single();
        clr.TargetSystem.Should().Be((byte)1);
        clr.TargetComponent.Should().Be((byte)1);
        clr.MissionType.Should().Be(MavMissionType.Mission);
    }

    [Fact]
    public async Task ClearAsync_happy_path_completes_Accepted_on_ACK()
    {
        var (client, conn) = Build();
        using var disp = client;

        var task = client.ClearAsync();
        conn.RaiseMissionAck(Vehicle11, Ack(MavMissionResult.MavMissionAccepted));

        var result = await task;
        result.Status.Should().Be(MissionTransactionStatus.Accepted);
        result.IsAccepted.Should().BeTrue();
        result.AckResult.Should().Be(MavMissionResult.MavMissionAccepted);
    }

    [Fact]
    public async Task ClearAsync_NACK_resolves_Rejected()
    {
        var (client, conn) = Build();
        using var disp = client;

        var task = client.ClearAsync();
        conn.RaiseMissionAck(Vehicle11, Ack(MavMissionResult.MavMissionDenied));

        var result = await task;
        result.Status.Should().Be(MissionTransactionStatus.Rejected);
        result.AckResult.Should().Be(MavMissionResult.MavMissionDenied);
    }

    [Fact]
    public async Task ClearAsync_no_ACK_retries_then_times_out()
    {
        var (client, conn) = Build(maxRetries: 2,
            defaultTimeout: TimeSpan.FromMilliseconds(30));
        using var disp = client;

        var task = client.ClearAsync();
        var result = await task;

        result.Status.Should().Be(MissionTransactionStatus.Timeout);
        // initial CLEAR_ALL + 2 retries.
        conn.SentMessages.OfType<MissionClearAll>().Should().HaveCount(3);
    }

    [Fact]
    public async Task ClearAsync_cancellation_resolves_Cancelled()
    {
        var (client, conn) = Build(
            defaultTimeout: TimeSpan.FromSeconds(10));
        using var disp = client;
        using var cts = new CancellationTokenSource();

        var task = client.ClearAsync(cts.Token);
        cts.Cancel();

        (await task).Status.Should().Be(MissionTransactionStatus.Cancelled);
    }

    [Fact]
    public async Task ClearAsync_concurrent_with_Upload_throws_InvalidOperationException()
    {
        var (client, conn) = Build(
            defaultTimeout: TimeSpan.FromSeconds(10),
            itemTimeout: TimeSpan.FromSeconds(10));
        using var disp = client;

        var upload = client.UploadAsync(ThreeWaypoints());

        Action act = () => { _ = client.ClearAsync(); };
        act.Should().Throw<InvalidOperationException>();

        conn.RaiseMissionAck(Vehicle11, Ack(MavMissionResult.MavMissionAccepted));
        await upload;
    }

    [Fact]
    public async Task ClearAsync_ack_for_wrong_mission_type_is_ignored()
    {
        var (client, conn) = Build(maxRetries: 0,
            defaultTimeout: TimeSpan.FromMilliseconds(50));
        using var disp = client;

        var task = client.ClearAsync();
        conn.RaiseMissionAck(Vehicle11, Ack(MavMissionResult.MavMissionAccepted, type: MavMissionType.Fence));

        (await task).Status.Should().Be(MissionTransactionStatus.Timeout,
            "the ACK was for a different MAV_MISSION_TYPE");
    }
}
