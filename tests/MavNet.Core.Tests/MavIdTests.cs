using FluentAssertions;
using MavNet.Core;
using Xunit;

namespace MavNet.Core.Tests;

public class MavIdTests
{
    [Fact]
    public void ToString_uses_sys_comp_format()
    {
        new MavId(1, 1).ToString().Should().Be("sys1-comp1");
        new MavId(255, 190).ToString().Should().Be("sys255-comp190");
        new MavId(0, 0).ToString().Should().Be("sys0-comp0");
    }

    [Fact]
    public void Equality_is_structural()
    {
        var a = new MavId(7, 42);
        var b = new MavId(7, 42);
        var c = new MavId(7, 43);

        a.Should().Be(b);
        a.Should().NotBe(c);
        a.GetHashCode().Should().Be(b.GetHashCode());
        (a == b).Should().BeTrue();
        (a != c).Should().BeTrue();
    }

    [Fact]
    public void Components_round_trip_through_constructor()
    {
        var id = new MavId(SystemId: 12, ComponentId: 34);
        id.SystemId.Should().Be((byte)12);
        id.ComponentId.Should().Be((byte)34);
    }
}
