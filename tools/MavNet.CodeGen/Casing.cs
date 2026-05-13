namespace MavNet.CodeGen;

/// <summary>SHOUTY_SNAKE_CASE → PascalCase, plus enum-entry prefix stripping.</summary>
internal static class Casing
{
    public static string PascalCase(string raw)
    {
        var parts = raw.Split('_', StringSplitOptions.RemoveEmptyEntries);
        var s = string.Concat(parts.Select(p =>
            char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant()));
        // C# identifiers can't start with a digit — fall back to underscore prefix.
        if (s.Length > 0 && char.IsDigit(s[0])) s = "_" + s;
        return s;
    }

    /// <summary>Drop the enum's own name prefix from an entry name, then PascalCase.
    /// E.g. ("MAV_CMD_NAV_TAKEOFF", "MAV_CMD") → "NavTakeoff".
    /// Some entries don't follow the prefix convention — keep them as-is then.</summary>
    public static string EntryName(string entryRaw, string enumRaw)
    {
        var prefix = enumRaw + "_";
        var stripped = entryRaw.StartsWith(prefix, StringComparison.Ordinal)
            ? entryRaw[prefix.Length..]
            : entryRaw;
        return PascalCase(stripped);
    }
}
