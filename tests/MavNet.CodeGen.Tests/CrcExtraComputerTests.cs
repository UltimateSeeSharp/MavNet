using System.IO;
using FluentAssertions;
using MavNet.CodeGen;
using Xunit;

namespace MavNet.CodeGen.Tests;

/// <summary>
/// Pins the CRC_EXTRA algorithm against the live spec. Promotes the in-tool SelfTest
/// to a proper xUnit theory so test-runner output (and CI) gates the build on the
/// algorithm staying correct. Any bug here means every generated message has the
/// wrong CrcExtra and PX4 drops every frame we send.
/// </summary>
public class CrcExtraComputerTests
{
    private static string SpecPath()
    {
        // Tests run from tests/MavNet.CodeGen.Tests/bin/.../net10.0; the spec lives 5 dirs up.
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "specs", "common.xml");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException("Could not locate specs/common.xml relative to test bin dir.");
    }

    private static readonly Spec Spec = XmlSpecParser.Load(SpecPath());

    [Theory]
    [InlineData("HEARTBEAT", (byte)50)]
    [InlineData("SYS_STATUS", (byte)124)]
    [InlineData("GPS_RAW_INT", (byte)24)]
    [InlineData("ATTITUDE", (byte)39)]
    [InlineData("GLOBAL_POSITION_INT", (byte)104)]
    [InlineData("VFR_HUD", (byte)20)]
    [InlineData("COMMAND_LONG", (byte)152)]
    [InlineData("COMMAND_ACK", (byte)143)]
    [InlineData("BATTERY_STATUS", (byte)154)]
    public void Compute_matches_published_crc_extra(string messageName, byte expected)
    {
        var msg = Spec.Messages.Single(m => m.Name == messageName);
        CrcExtraComputer.Compute(msg).Should().Be(expected,
            $"the published CRC_EXTRA for {messageName} is {expected}");
    }

    [Fact]
    public void SelfTest_passes_on_real_spec()
    {
        // If this throws, the bundled spec disagrees with one of the known-good values
        // in CrcExtraComputer.Expected — either the spec drifted or the algorithm did.
        var act = () => CrcExtraComputer.SelfTest(Spec);
        act.Should().NotThrow();
    }
}
