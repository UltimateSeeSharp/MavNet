using FluentAssertions;
using MavNet.CodeGen;
using Xunit;

namespace MavNet.CodeGen.Tests;

public class CasingTests
{
    [Theory]
    [InlineData("HEARTBEAT", "Heartbeat")]
    [InlineData("COMMAND_LONG", "CommandLong")]
    [InlineData("GLOBAL_POSITION_INT", "GlobalPositionInt")]
    [InlineData("MAV_CMD", "MavCmd")]
    [InlineData("uint8_t_mavlink_version", "Uint8TMavlinkVersion")]
    [InlineData("ATTITUDE", "Attitude")]
    public void PascalCase_lowercases_then_titles_each_underscore_part(string raw, string expected)
    {
        Casing.PascalCase(raw).Should().Be(expected);
    }

    [Fact]
    public void PascalCase_inserts_underscore_prefix_for_leading_digit()
    {
        Casing.PascalCase("3D_FIX").Should().Be("_3dFix",
            "C# identifiers may not start with a digit");
    }

    [Fact]
    public void PascalCase_skips_consecutive_underscores()
    {
        Casing.PascalCase("FOO__BAR").Should().Be("FooBar");
    }

    [Fact]
    public void EntryName_strips_enum_prefix()
    {
        Casing.EntryName("MAV_CMD_NAV_TAKEOFF", "MAV_CMD").Should().Be("NavTakeoff");
        Casing.EntryName("MAV_RESULT_ACCEPTED", "MAV_RESULT").Should().Be("Accepted");
    }

    [Fact]
    public void EntryName_keeps_value_when_prefix_does_not_match()
    {
        // Some entries don't follow the prefix convention — keep them as-is then PascalCase.
        Casing.EntryName("CUSTOM_VALUE", "MAV_CMD").Should().Be("CustomValue");
    }
}
