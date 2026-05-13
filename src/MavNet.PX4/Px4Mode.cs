namespace MavNet.PX4;

/// <summary>
/// PX4's <c>custom_mode</c> field on HEARTBEAT is a packed uint32: main mode in
/// byte 2, sub-mode in byte 3. This helper unpacks it into a human-readable label
/// matching the conventional PX4 mode strings (<c>AUTO.LOITER</c>, <c>MANUAL</c>, etc.).
/// </summary>
public static class Px4Mode
{
    /// <summary>Unpacks a PX4 <c>custom_mode</c> uint32 into a human-readable mode label (e.g. <c>"AUTO.LOITER"</c>).</summary>
    public static string Format(uint customMode)
    {
        var mainMode = (byte)((customMode >> 16) & 0xFF);
        var subMode  = (byte)((customMode >> 24) & 0xFF);
        return mainMode switch
        {
            1 => "MANUAL",
            2 => "ALTCTL",
            3 => "POSCTL",
            4 => subMode switch
            {
                1 => "AUTO.READY",
                2 => "AUTO.TAKEOFF",
                3 => "AUTO.LOITER",
                4 => "AUTO.MISSION",
                5 => "AUTO.RTL",
                6 => "AUTO.LAND",
                7 => "AUTO.RTGS",
                8 => "AUTO.FOLLOW",
                _ => "AUTO"
            },
            5 => "ACRO",
            6 => "OFFBOARD",
            7 => "STABILIZED",
            8 => "RATTITUDE",
            _ => $"CUSTOM:{customMode}"
        };
    }
}
