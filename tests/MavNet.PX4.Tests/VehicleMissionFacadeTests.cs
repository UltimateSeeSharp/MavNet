using FluentAssertions;
using MavNet.Core;
using MavNet.PX4.Missions;
using MavNet.Protocol.Generated.Enums;
using MavNet.Protocol.Generated.Messages;
using Xunit;

namespace MavNet.PX4.Tests;

/// <summary>
/// Vehicle exposes nine mission-protocol methods (upload/download/clear × waypoint/fence/rally)
/// backed by three independent <see cref="MissionClient"/>s — one per <c>MAV_MISSION_TYPE</c>.
/// These tests confirm that:
///
/// 1. Each facade method routes to the right mission_type on the wire.
/// 2. The three clients are independent — uploading a fence does not block uploading rally
///    points, and an inbound event for one type doesn't trigger another's state machine.
///
/// Per spec ("The mission types must be stored and handled separately/independently"), this
/// is the critical invariant for multi-type mission support.
/// </summary>
public class VehicleMissionFacadeTests
{
    private static readonly MavId Vehicle11 = new(1, 1);

    private static (TestDrone Drone, FakeMavlinkConnection Conn) Build()
    {
        var conn = new FakeMavlinkConnection();
        var drone = new TestDrone(conn);
        return (drone, conn);
    }

    private static MissionItem[] OneWaypoint() => new[]
    {
        MissionItem.Waypoint(47.0, 8.5, 30f),
    };

    [Fact]
    public async Task UploadFenceAsync_sends_MISSION_COUNT_with_FENCE_type()
    {
        var (drone, conn) = Build();
        _ = drone.UploadFenceAsync(OneWaypoint());
        await Task.Delay(20);

        var count = conn.SentMessages.OfType<MissionCount>().Single();
        count.MissionType.Should().Be(MavMissionType.Fence);
    }

    [Fact]
    public async Task UploadRallyAsync_sends_MISSION_COUNT_with_RALLY_type()
    {
        var (drone, conn) = Build();
        _ = drone.UploadRallyAsync(OneWaypoint());
        await Task.Delay(20);

        var count = conn.SentMessages.OfType<MissionCount>().Single();
        count.MissionType.Should().Be(MavMissionType.Rally);
    }

    [Fact]
    public async Task ClearFenceAsync_sends_MISSION_CLEAR_ALL_with_FENCE_type()
    {
        var (drone, conn) = Build();
        _ = drone.ClearFenceAsync();
        await Task.Delay(20);

        var clr = conn.SentMessages.OfType<MissionClearAll>().Single();
        clr.MissionType.Should().Be(MavMissionType.Fence);
    }

    [Fact]
    public async Task DownloadRallyAsync_sends_MISSION_REQUEST_LIST_with_RALLY_type()
    {
        var (drone, conn) = Build();
        _ = drone.DownloadRallyAsync();
        await Task.Delay(20);

        var rq = conn.SentMessages.OfType<MissionRequestList>().Single();
        rq.MissionType.Should().Be(MavMissionType.Rally);
    }

    [Fact]
    public async Task Three_concurrent_uploads_run_independently()
    {
        // Spec: "The mission types must be stored and handled separately/independently."
        // Upload waypoint + fence + rally in parallel. Each must accept its own ACK without
        // interference.
        var (drone, conn) = Build();

        var mission = drone.UploadMissionAsync(OneWaypoint());
        var fence   = drone.UploadFenceAsync(OneWaypoint());
        var rally   = drone.UploadRallyAsync(OneWaypoint());

        // Each ACK targets only its type; the other two clients ignore it.
        conn.RaiseMissionAck(Vehicle11, new MissionAck(255, 190,
            MavMissionResult.MavMissionAccepted, MavMissionType.Mission, OpaqueId: 0x111));
        conn.RaiseMissionAck(Vehicle11, new MissionAck(255, 190,
            MavMissionResult.MavMissionAccepted, MavMissionType.Fence, OpaqueId: 0x222));
        conn.RaiseMissionAck(Vehicle11, new MissionAck(255, 190,
            MavMissionResult.MavMissionAccepted, MavMissionType.Rally, OpaqueId: 0x333));

        var results = await Task.WhenAll(mission, fence, rally);

        results[0].IsAccepted.Should().BeTrue();
        results[0].OpaqueId.Should().Be(0x111u, "mission client must consume the Mission ACK");
        results[1].IsAccepted.Should().BeTrue();
        results[1].OpaqueId.Should().Be(0x222u, "fence client must consume the Fence ACK");
        results[2].IsAccepted.Should().BeTrue();
        results[2].OpaqueId.Should().Be(0x333u, "rally client must consume the Rally ACK");
    }

    [Fact]
    public async Task Fence_request_does_not_trigger_mission_client()
    {
        // If an upload is in flight for type=Mission only, an inbound REQUEST_INT for
        // type=Fence must NOT cause the mission client to send a fence item.
        var (drone, conn) = Build();
        var mission = drone.UploadMissionAsync(OneWaypoint());

        conn.RaiseMissionRequestInt(Vehicle11,
            new MissionRequestInt(0, 1, 1, MavMissionType.Fence));

        await Task.Delay(50);
        conn.SentMessages.OfType<MissionItemInt>().Should().BeEmpty(
            "no fence client is active; the mission client must ignore the Fence-typed request");

        conn.RaiseMissionAck(Vehicle11, new MissionAck(255, 190,
            MavMissionResult.MavMissionAccepted, MavMissionType.Mission, 0));
        await mission;
    }

    [Fact]
    public async Task Disposing_Vehicle_cancels_in_flight_transactions_across_all_types()
    {
        var (drone, _) = Build();
        var mission = drone.UploadMissionAsync(OneWaypoint());
        var fence   = drone.UploadFenceAsync(OneWaypoint());
        var rally   = drone.UploadRallyAsync(OneWaypoint());

        await drone.DisposeAsync();

        (await mission).Status.Should().Be(MissionTransactionStatus.Cancelled);
        (await fence).Status.Should().Be(MissionTransactionStatus.Cancelled);
        (await rally).Status.Should().Be(MissionTransactionStatus.Cancelled);
    }
}
