using MavNet.Protocol.Generated.Enums;

namespace MavNet.PX4.Missions;

/// <summary>
/// Terminal state of one mission-clear transaction.
/// </summary>
/// <param name="Status">High-level outcome category — see <see cref="MissionTransactionStatus"/>.</param>
/// <param name="AckResult">Raw <c>MISSION_ACK.type</c> if the vehicle ACKed, or <c>null</c>
/// if the transaction ended without an ACK.</param>
/// <param name="Elapsed">Wall-clock duration from clear start to terminal outcome.</param>
public readonly record struct MissionClearResult(
    MissionTransactionStatus Status,
    MavMissionResult? AckResult,
    TimeSpan Elapsed)
{
    /// <summary>True iff <see cref="Status"/> is <see cref="MissionTransactionStatus.Accepted"/>.</summary>
    public bool IsAccepted => Status == MissionTransactionStatus.Accepted;
}
