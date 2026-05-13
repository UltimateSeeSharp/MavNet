using System.Text;
using MavNet.Protocol;

namespace MavNet.CodeGen;

/// <summary>
/// Computes the per-message CRC_EXTRA byte that MAVLink mixes into every frame's CRC.
/// Algorithm (per MAVLink spec / pymavlink):
///   CRC-16/X.25 over: "&lt;msg_name&gt; " + for each NON-EXTENSION field IN WIRE ORDER:
///       "&lt;type&gt; &lt;name&gt; "  (type without array brackets, uint8_t_mavlink_version → uint8_t)
///       and if the field is an array, accumulate one more byte = array_length
///   Final: (crc &amp; 0xff) XOR (crc &gt;&gt; 8)
/// </summary>
internal static class CrcExtraComputer
{
    public static byte Compute(MessageSpec msg)
    {
        ushort crc = 0xFFFF;
        crc = Crc16.Accumulate(Encoding.ASCII.GetBytes(msg.Name + " "), crc);
        foreach (var f in msg.NonExtensionFields)
        {
            crc = Crc16.Accumulate(Encoding.ASCII.GetBytes(f.Type.CrcType + " "), crc);
            crc = Crc16.Accumulate(Encoding.ASCII.GetBytes(f.Name + " "), crc);
            if (f.Type.IsArray)
                crc = Crc16.Accumulate((byte)f.Type.ArrayLength, crc);
        }
        return (byte)((crc & 0xff) ^ (crc >> 8));
    }

    /// <summary>Known-good CRC_EXTRA values from the MAVLink spec / observed PX4 wire traffic.
    /// Generator aborts if any disagree — that catches algorithm bugs before garbage messages ship.</summary>
    public static readonly IReadOnlyDictionary<string, byte> Expected = new Dictionary<string, byte>
    {
        ["HEARTBEAT"]            = 50,
        ["COMMAND_LONG"]         = 152,
        ["COMMAND_ACK"]          = 143,
        ["GLOBAL_POSITION_INT"]  = 104,
        ["SYS_STATUS"]           = 124,
        ["GPS_RAW_INT"]          = 24,
        ["ATTITUDE"]             = 39,
        ["VFR_HUD"]              = 20,
        ["BATTERY_STATUS"]       = 154,
    };

    public static void SelfTest(Spec spec)
    {
        var failures = new List<string>();
        foreach (var (name, expected) in Expected)
        {
            var msg = spec.Messages.FirstOrDefault(m => m.Name == name);
            if (msg is null) continue;                       // not in dialect — skip
            var actual = Compute(msg);
            if (actual != expected)
                failures.Add($"  {name}: expected CRC_EXTRA={expected}, computed {actual}");
        }
        if (failures.Count > 0)
            throw new InvalidOperationException(
                "CRC_EXTRA self-test failed — algorithm is wrong, refusing to emit:\n" +
                string.Join("\n", failures));
    }
}
