namespace MavNet.CodeGen;

/// <summary>
/// MAVLink primitive type system. Knows wire size (for sort + CRC), C# type
/// representation, and whether it's an array (with element count).
/// </summary>
internal sealed record MavType(string Wire, int ElementSize, int ArrayLength, string CSharpElement)
{
    public bool IsArray => ArrayLength > 1;
    public int WireSize => ElementSize * ArrayLength;

    /// <summary>Type as written in CRC_EXTRA computation — without [N] for arrays.
    /// `uint8_t_mavlink_version` is rendered as plain `uint8_t` per MAVLink spec.</summary>
    public string CrcType => Wire == "uint8_t_mavlink_version" ? "uint8_t" : Wire;

    /// <summary>C# type for a field of this MAVLink type — array becomes `byte[]`, char arrays surface as `string`.</summary>
    public string CSharpFieldType => IsArray
        ? (Wire == "char" ? "string" : $"{CSharpElement}[]")
        : CSharpElement;

    public static MavType Parse(string raw)
    {
        // raw might be "uint8_t", "char[16]", "uint16_t[10]", "uint8_t_mavlink_version"
        var bracket = raw.IndexOf('[');
        var bare = bracket < 0 ? raw : raw[..bracket];
        var arrayLen = 1;
        if (bracket >= 0)
        {
            var close = raw.IndexOf(']', bracket);
            arrayLen = int.Parse(raw[(bracket + 1)..close]);
        }
        var (elementSize, csElement) = bare switch
        {
            "uint8_t"                  => (1, "byte"),
            "int8_t"                   => (1, "sbyte"),
            "uint8_t_mavlink_version"  => (1, "byte"),
            "char"                     => (1, "byte"),    // we surface arrays-of-char as `string` via FieldType
            "uint16_t"                 => (2, "ushort"),
            "int16_t"                  => (2, "short"),
            "uint32_t"                 => (4, "uint"),
            "int32_t"                  => (4, "int"),
            "float"                    => (4, "float"),
            "uint64_t"                 => (8, "ulong"),
            "int64_t"                  => (8, "long"),
            "double"                   => (8, "double"),
            _ => throw new ArgumentException($"Unknown MAVLink type '{bare}' in '{raw}'")
        };
        return new MavType(bare, elementSize, arrayLen, csElement);
    }
}
