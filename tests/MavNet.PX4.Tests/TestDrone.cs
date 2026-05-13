using MavNet.Transport.Udp;

namespace MavNet.PX4.Tests;

/// <summary>
/// Concrete <see cref="MavNet.PX4.Base.Vehicle"/> subclass used by tests. <see cref="Drone"/>
/// itself is sealed with a private constructor, so the test layer subclasses the abstract base
/// directly. Defaults to a 200 ms command timeout so timeout-driven assertions stay fast.
/// </summary>
internal sealed class TestDrone : MavNet.PX4.Base.Vehicle
{
    public TestDrone(IMavlinkConnection conn, byte sys = 1, byte comp = 1,
        TimeSpan? commandTimeout = null, TimeSpan? heartbeatTimeout = null)
        : base(conn, sys, comp,
               ownsConnection: false,
               heartbeatTimeout: heartbeatTimeout,
               commandTimeout: commandTimeout ?? TimeSpan.FromMilliseconds(200))
    {
    }

    public (double Lat, double Lon, bool Armed, double Battery, bool LinkUp) Snapshot() =>
        (Lat, Lon, Armed, Battery, LinkUp);
}
