namespace MavNet.PX4.Missions;

/// <summary>
/// Terminal state of one mission download transaction.
/// </summary>
/// <param name="Status">High-level outcome category — see <see cref="MissionTransactionStatus"/>.</param>
/// <param name="Items">The downloaded items, in sequence order. Populated only on
/// <see cref="MissionTransactionStatus.Accepted"/>; empty for every other outcome.</param>
/// <param name="OpaqueId">On-vehicle plan id from the <c>MISSION_COUNT</c> sent by the vehicle.
/// Populated only on <see cref="MissionTransactionStatus.Accepted"/>. Use this to detect
/// whether the on-vehicle mission has changed since a prior download/upload.</param>
/// <param name="Elapsed">Wall-clock duration from download start to terminal outcome.</param>
public readonly record struct MissionDownloadResult(
    MissionTransactionStatus Status,
    IReadOnlyList<MissionItem> Items,
    uint? OpaqueId,
    TimeSpan Elapsed)
{
    /// <summary>True iff <see cref="Status"/> is <see cref="MissionTransactionStatus.Accepted"/>.</summary>
    public bool IsAccepted => Status == MissionTransactionStatus.Accepted;
}
