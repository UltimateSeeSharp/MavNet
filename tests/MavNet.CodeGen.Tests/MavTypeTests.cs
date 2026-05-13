using FluentAssertions;
using MavNet.CodeGen;
using Xunit;

namespace MavNet.CodeGen.Tests;

public class MavTypeTests
{
    [Theory]
    [InlineData("uint8_t", 1, 1, "byte")]
    [InlineData("int8_t", 1, 1, "sbyte")]
    [InlineData("uint16_t", 2, 1, "ushort")]
    [InlineData("int16_t", 2, 1, "short")]
    [InlineData("uint32_t", 4, 1, "uint")]
    [InlineData("int32_t", 4, 1, "int")]
    [InlineData("float", 4, 1, "float")]
    [InlineData("uint64_t", 8, 1, "ulong")]
    [InlineData("int64_t", 8, 1, "long")]
    [InlineData("double", 8, 1, "double")]
    [InlineData("char", 1, 1, "byte")]
    public void Parses_primitive_types(string raw, int elementSize, int len, string cs)
    {
        var t = MavType.Parse(raw);
        t.ElementSize.Should().Be(elementSize);
        t.ArrayLength.Should().Be(len);
        t.CSharpElement.Should().Be(cs);
        t.IsArray.Should().BeFalse();
        t.WireSize.Should().Be(elementSize);
    }

    [Theory]
    [InlineData("uint8_t[16]", 1, 16, 16)]
    [InlineData("float[4]", 4, 4, 16)]
    [InlineData("int32_t[3]", 4, 3, 12)]
    [InlineData("char[50]", 1, 50, 50)]
    public void Parses_array_types(string raw, int elementSize, int len, int wireSize)
    {
        var t = MavType.Parse(raw);
        t.ElementSize.Should().Be(elementSize);
        t.ArrayLength.Should().Be(len);
        t.IsArray.Should().BeTrue();
        t.WireSize.Should().Be(wireSize);
    }

    [Fact]
    public void Char_arrays_surface_as_string()
    {
        MavType.Parse("char[50]").CSharpFieldType.Should().Be("string");
    }

    [Fact]
    public void Byte_arrays_surface_as_byte_array()
    {
        MavType.Parse("uint8_t[16]").CSharpFieldType.Should().Be("byte[]");
    }

    [Fact]
    public void Uint8_t_mavlink_version_is_a_real_field_type_but_uses_uint8_t_for_CRC()
    {
        // Per the MAVLink spec the heartbeat's mavlink_version field uses the special
        // typename `uint8_t_mavlink_version` to mark it as the protocol version byte —
        // but for CRC_EXTRA computation it must be treated as plain `uint8_t`.
        var t = MavType.Parse("uint8_t_mavlink_version");
        t.CSharpElement.Should().Be("byte");
        t.CrcType.Should().Be("uint8_t");
        t.WireSize.Should().Be(1);
    }

    [Fact]
    public void Unknown_type_throws()
    {
        var act = () => MavType.Parse("widget_t");
        act.Should().Throw<System.ArgumentException>();
    }
}
