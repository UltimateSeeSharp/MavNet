using System.Net;
using FluentAssertions;
using MavNet.Protocol.Generated.Enums;
using MavNet.Protocol.Generated.Messages;
using MavNet.PX4.Vehicles;
using MavNet.Transport.Udp;
using Xunit;

namespace MavNet.PX4.Tests;

/// <summary>
/// End-to-end test of <see cref="Drone.ConnectAsync"/>: opens a real UDP transport,
/// waits for the first heartbeat, and either resolves into a Drone or raises TimeoutException.
/// Uses loopback sockets so it's deterministic on CI.
/// </summary>
public class DroneConnectAsyncTests
{
    [Fact]
    public async Task Resolves_to_Drone_when_heartbeat_arrives()
    {
        // Allocate two ephemeral ports for a deterministic handshake.
        int dronePort = PickEphemeralPort();
        int probePort = PickEphemeralPort();

        // We need ConnectAsync to bind dronePort (local) and target probePort (remote).
        // From the probe side: bind probePort and send to dronePort.
        var probe = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, probePort));

        try
        {
            var cs = $"udp://127.0.0.1:{dronePort}?rhost=127.0.0.1&rport={probePort}";
            var connectTask = Drone.ConnectAsync(cs, TimeSpan.FromSeconds(2));

            // Send a HEARTBEAT from the probe to the drone's local port.
            await Task.Delay(50); // small grace so the receive loop is up
            var hb = new Heartbeat(0, MavType.Quadrotor, MavAutopilot.Px4,
                MavModeFlag.SafetyArmed, MavState.Active, 3);
            var payload = new byte[Heartbeat.MaxPayloadLength];
            hb.Encode(payload);
            var frame = BuildFrame(Heartbeat.MsgId, Heartbeat.CrcExtra, payload);
            probe.SendTo(frame, new IPEndPoint(IPAddress.Loopback, dronePort));

            var drone = await connectTask;
            drone.Should().NotBeNull();
            drone.SystemId.Should().Be((byte)1);
            drone.ComponentId.Should().Be((byte)1);
            await drone.DisposeAsync();
        }
        finally
        {
            probe.Dispose();
        }
    }

    [Fact]
    public async Task Throws_TimeoutException_when_no_heartbeat_arrives()
    {
        int dronePort = PickEphemeralPort();
        int probePort = PickEphemeralPort();

        var cs = $"udp://127.0.0.1:{dronePort}?rhost=127.0.0.1&rport={probePort}";
        var act = async () => await Drone.ConnectAsync(cs, TimeSpan.FromMilliseconds(200));

        await act.Should().ThrowAsync<TimeoutException>();
    }

    private static int PickEphemeralPort()
    {
        using var s = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
        s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)s.LocalEndPoint!).Port;
    }

    private static byte[] BuildFrame(uint msgId, byte crcExtra, byte[] payload)
    {
        var frame = new byte[12 + payload.Length];
        frame[0] = 0xFD;
        frame[1] = (byte)payload.Length;
        frame[2] = 0; frame[3] = 0; frame[4] = 1; frame[5] = 1; frame[6] = 1;
        frame[7] = (byte)(msgId & 0xFF);
        frame[8] = (byte)((msgId >> 8) & 0xFF);
        frame[9] = (byte)((msgId >> 16) & 0xFF);
        payload.CopyTo(frame, 10);

        var crc = MavNet.Protocol.Crc16.Accumulate(frame.AsSpan(1, 9 + payload.Length));
        crc = MavNet.Protocol.Crc16.Accumulate(crcExtra, crc);
        frame[10 + payload.Length] = (byte)(crc & 0xFF);
        frame[11 + payload.Length] = (byte)((crc >> 8) & 0xFF);
        return frame;
    }
}
