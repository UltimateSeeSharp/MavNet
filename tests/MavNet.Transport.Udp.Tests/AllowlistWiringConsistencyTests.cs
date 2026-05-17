using System.Reflection;
using FluentAssertions;
using MavNet.Core;
using MavNet.Protocol;
using MavNet.Protocol.Generated;
using MavNet.Protocol.Generated.Messages;
using MavNet.Transport.Udp;
using Xunit;

namespace MavNet.Transport.Udp.Tests;

/// <summary>
/// Guards the allowlist → <see cref="IMavlinkConnection"/> wiring ritual in
/// CLAUDE.md (codegen step 2): adding a message to <c>allowlist.txt</c> and
/// regenerating must be followed by a matching
/// <c>event Action&lt;MavId, T, DateTime&gt;</c> on the interface and its
/// implementation. This fails CI if the event half is forgotten.
///
/// <para>Limits: it cannot see the private <c>case T.MsgId:</c> arm in
/// <c>MavlinkConnection</c>'s dispatcher — that stays covered by the
/// per-message facts in <see cref="MavlinkConnectionDispatchTests"/> (a
/// missing arm makes the roundtrip fact time out). The two together
/// discharge the invariant.</para>
///
/// <para>Send-only messages have no inbound event by design and are listed
/// explicitly in <see cref="SendOnly"/>. A new send-only allowlist entry
/// surfacing here is intended friction — add it there with justification,
/// don't weaken the test.</para>
/// </summary>
public class AllowlistWiringConsistencyTests
{
    /// <summary>
    /// Spec names (<see cref="MessageInfo.Name"/>) of allowlisted messages
    /// that are SEND-only and therefore deliberately expose no inbound
    /// <c>*Received</c> event. COMMAND_LONG is sent by the GCS, never received.
    /// </summary>
    private static readonly HashSet<string> SendOnly = new(StringComparer.Ordinal)
    {
        "COMMAND_LONG",
    };

    // The set of generated message structs IS the allowlist, by construction
    // (the emitter writes exactly one struct per allowlist entry). Reflecting
    // the assembly is equivalent to parsing allowlist.txt and is robust to
    // codegen path/layout changes.
    private static List<Type> AllowlistedMessageTypes() =>
        typeof(CommandAck).Assembly
            .GetTypes()
            .Where(t => t.IsValueType && !t.IsEnum
                && t.GetInterfaces().Any(i =>
                    i.IsGenericType
                    && i.GetGenericTypeDefinition() == typeof(IMavlinkMessage<>)
                    && i.GetGenericArguments()[0] == t))
            .ToList();

    private static string SpecName(Type messageType)
    {
        var msgId = (uint)messageType
            .GetField(nameof(CommandAck.MsgId), BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null)!;
        return MessageRegistry.All.Single(m => m.MsgId == msgId).Name;
    }

    // The T in every `event Action<MavId, T, DateTime>` on the given type.
    private static HashSet<Type> InboundEventMessageTypes(Type connectionType) =>
        connectionType.GetEvents()
            .Select(e => e.EventHandlerType!)
            .Where(h => h.IsGenericType && h.GetGenericTypeDefinition() == typeof(Action<,,>))
            .Select(h => h.GetGenericArguments())
            .Where(a => a[0] == typeof(MavId) && a[2] == typeof(DateTime))
            .Select(a => a[1])
            .ToHashSet();

    [Fact]
    public void Reflection_finds_the_allowlisted_messages()
    {
        // Fails loudly if the CRTP/codegen shape changes, so the wiring
        // assertions below can never pass vacuously on an empty set.
        AllowlistedMessageTypes().Should().HaveCountGreaterThan(10);
    }

    [Fact]
    public void Every_inbound_allowlisted_message_has_an_IMavlinkConnection_event()
    {
        var wired = InboundEventMessageTypes(typeof(IMavlinkConnection));

        var missing = AllowlistedMessageTypes()
            .Where(t => !SendOnly.Contains(SpecName(t)))
            .Where(t => !wired.Contains(t))
            .Select(t => $"{SpecName(t)} ({t.Name})")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        missing.Should().BeEmpty(
            "every inbound allowlisted message needs a matching "
            + "`event Action<MavId, T, DateTime>` on IMavlinkConnection — run "
            + "the CLAUDE.md codegen step-2 ritual (event + dispatcher case arm "
            + $"+ tests), or add a genuinely send-only message to {nameof(SendOnly)}. "
            + "Missing: {0}", string.Join(", ", missing));
    }

    [Fact]
    public void MavlinkConnection_exposes_every_event_declared_on_the_interface()
    {
        var iface = InboundEventMessageTypes(typeof(IMavlinkConnection));
        var impl = InboundEventMessageTypes(typeof(MavlinkConnection));

        iface.Should().BeSubsetOf(impl,
            "the concrete MavlinkConnection must implement every typed event "
            + "declared on IMavlinkConnection (interface-vs-impl drift is the "
            + "other half of the codegen step-2 ritual).");
    }
}
