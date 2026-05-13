using System.Linq;
using System.Reflection;
using FluentAssertions;
using MavNet.Protocol;
using MavNet.Protocol.Generated.Messages;
using Xunit;

namespace MavNet.Protocol.Generated.Tests;

/// <summary>
/// Cross-cutting checks on the generated message assembly. Verifies the emitter's
/// invariants haven't drifted: every IMavlinkMessage closes over itself (CRTP),
/// every message struct's static MsgId is also present in the runtime registry,
/// and every constant pair (MsgId/CrcExtra/Min/Max) matches what the registry says.
/// </summary>
public class MessageContractTests
{
    private static IEnumerable<System.Type> AllMessageTypes()
    {
        var asm = typeof(Heartbeat).Assembly;
        return asm.GetTypes().Where(t =>
            t is { IsValueType: true, IsPublic: true } &&
            t.GetInterfaces().Any(i => i.IsGenericType &&
                i.GetGenericTypeDefinition() == typeof(IMavlinkMessage<>)));
    }

    [Fact]
    public void Generated_assembly_contains_at_least_the_allowlisted_messages()
    {
        var types = AllMessageTypes().ToList();
        types.Select(t => t.Name).Should().Contain(new[]
        {
            nameof(Heartbeat), nameof(SysStatus), nameof(GpsRawInt),
            nameof(GlobalPositionInt), nameof(VfrHud),
            nameof(CommandLong), nameof(CommandAck), nameof(ExtendedSysState),
        });
    }

    [Fact]
    public void Every_message_struct_self_closes_the_CRTP_interface()
    {
        foreach (var t in AllMessageTypes())
        {
            var closed = t.GetInterfaces().Single(i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IMavlinkMessage<>));
            closed.GenericTypeArguments[0].Should().Be(t,
                $"{t.Name} must implement IMavlinkMessage<{t.Name}>");
        }
    }

    [Fact]
    public void Every_message_struct_metadata_matches_registry()
    {
        foreach (var t in AllMessageTypes())
        {
            var msgIdField = t.GetField("MsgId", BindingFlags.Public | BindingFlags.Static)!;
            var crcExtraField = t.GetField("CrcExtra", BindingFlags.Public | BindingFlags.Static)!;
            var minField = t.GetField("MinPayloadLength", BindingFlags.Public | BindingFlags.Static)!;
            var maxField = t.GetField("MaxPayloadLength", BindingFlags.Public | BindingFlags.Static)!;

            var msgId = (uint)msgIdField.GetValue(null)!;
            var crcExtra = (byte)crcExtraField.GetValue(null)!;
            var min = (byte)minField.GetValue(null)!;
            var max = (byte)maxField.GetValue(null)!;

            MessageRegistry.TryGet(msgId, out var info).Should().BeTrue(
                $"{t.Name} (MsgId={msgId}) must be present in MessageRegistry");
            info.CrcExtra.Should().Be(crcExtra, $"{t.Name} CrcExtra mismatch with registry");
            info.MinPayloadLength.Should().Be(min, $"{t.Name} MinPayloadLength mismatch with registry");
            info.MaxPayloadLength.Should().Be(max, $"{t.Name} MaxPayloadLength mismatch with registry");
        }
    }
}
