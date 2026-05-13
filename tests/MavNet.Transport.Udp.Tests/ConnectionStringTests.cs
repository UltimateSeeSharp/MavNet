using System.Net;
using FluentAssertions;
using MavNet.Transport.Udp;
using Xunit;

namespace MavNet.Transport.Udp.Tests;

public class ConnectionStringTests
{
    [Fact]
    public void Fully_specified_string_parses_to_expected_endpoints()
    {
        var (local, remote) = ConnectionString.Parse("udp://0.0.0.0:14550?rhost=127.0.0.1&rport=18570");

        local.Should().Be(new IPEndPoint(IPAddress.Any, 14550));
        remote.Should().Be(new IPEndPoint(IPAddress.Loopback, 18570));
    }

    [Fact]
    public void Bare_string_without_scheme_is_treated_as_udp()
    {
        var (local, _) = ConnectionString.Parse("127.0.0.1:14551");
        local.Should().Be(new IPEndPoint(IPAddress.Loopback, 14551));
    }

    [Fact]
    public void Defaults_apply_when_query_is_omitted()
    {
        var (local, remote) = ConnectionString.Parse("udp://0.0.0.0:14550");

        local.Should().Be(new IPEndPoint(IPAddress.Any, 14550));
        remote.Should().Be(new IPEndPoint(IPAddress.Loopback, 18570));
    }

    [Fact]
    public void Only_rhost_specified_keeps_default_rport()
    {
        var (_, remote) = ConnectionString.Parse("udp://0.0.0.0:14550?rhost=192.168.1.10");
        remote.Should().Be(new IPEndPoint(IPAddress.Parse("192.168.1.10"), 18570));
    }

    [Fact]
    public void Only_rport_specified_keeps_default_rhost()
    {
        var (_, remote) = ConnectionString.Parse("udp://0.0.0.0:14550?rport=4242");
        remote.Should().Be(new IPEndPoint(IPAddress.Loopback, 4242));
    }

    [Fact]
    public void Query_param_keys_are_case_insensitive()
    {
        var (_, remote) = ConnectionString.Parse("udp://0.0.0.0:14550?RHOST=10.0.0.1&RPORT=9000");
        remote.Should().Be(new IPEndPoint(IPAddress.Parse("10.0.0.1"), 9000));
    }

    [Fact]
    public void Non_udp_scheme_throws()
    {
        var act = () => ConnectionString.Parse("tcp://0.0.0.0:14550");
        act.Should().Throw<System.ArgumentException>().WithMessage("*udp*");
    }

    [Fact]
    public void Malformed_rport_falls_back_to_default()
    {
        // int.TryParse returns false on garbage; the existing implementation leaves rport at the default.
        var (_, remote) = ConnectionString.Parse("udp://0.0.0.0:14550?rport=not-a-number");
        remote.Port.Should().Be(18570);
    }
}
