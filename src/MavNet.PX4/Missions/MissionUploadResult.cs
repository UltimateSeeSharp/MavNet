using MavNet.Protocol.Generated.Enums;

namespace MavNet.PX4.Missions;

/// <summary>How a mission upload transaction terminated.</summary>
public enum MissionTransactionStatus
{
    /// <summary>Vehicle responded with <see cref="MavMissionResult.MavMissionAccepted"/>.
    /// The mission is now stored on the vehicle; <see cref="MissionUploadResult.OpaqueId"/>
    /// is the new on-vehicle plan id.</summary>
    Accepted,
    /// <summary>Vehicle responded with a non-Accepted <c>MISSION_ACK</c>. Per spec these
    /// are unrecoverable — both ends reset to idle and the prior mission is unchanged.
    /// <see cref="MissionUploadResult.AckResult"/> carries the specific error code.</summary>
    Rejected,
    /// <summary>No <c>MISSION_REQUEST(_INT)</c> or <c>MISSION_ACK</c> arrived within the
    /// retry budget. Both ends reset to idle; the prior mission is unchanged.</summary>
    Timeout,
    /// <summary>The caller's <see cref="System.Threading.CancellationToken"/> fired before
    /// the transaction completed. No final state is implied — the vehicle may have a
    /// partial upload, so a follow-up clear or re-upload is advisable.</summary>
    Cancelled,
}

/// <summary>
/// Terminal state of one mission upload transaction.
/// </summary>
/// <param name="Status">High-level outcome category — see <see cref="MissionTransactionStatus"/>.</param>
/// <param name="AckResult">Raw <c>MISSION_ACK.type</c> if the vehicle ACKed, or <c>null</c>
/// if the transaction ended without an ACK (timeout, cancellation).</param>
/// <param name="OpaqueId">The new on-vehicle plan id from <c>MISSION_ACK.opaque_id</c>,
/// populated only on <see cref="MissionTransactionStatus.Accepted"/>. Use this to detect
/// whether the on-vehicle mission has been replaced (vs. comparing waypoint contents).</param>
/// <param name="Elapsed">Wall-clock duration from upload start to terminal outcome.</param>
public readonly record struct MissionUploadResult(
    MissionTransactionStatus Status,
    MavMissionResult? AckResult,
    uint? OpaqueId,
    TimeSpan Elapsed)
{
    /// <summary>True iff <see cref="Status"/> is <see cref="MissionTransactionStatus.Accepted"/>.</summary>
    public bool IsAccepted => Status == MissionTransactionStatus.Accepted;
}
