using System.Net;
using System.Net.Sockets;

namespace MavNet.Transport.Udp.Tests;

/// <summary>
/// Test-side UDP socket used as the "other end" of a <see cref="MavlinkConnection"/>.
/// Binds on loopback at an OS-assigned port and exposes blocking receive with a timeout.
/// </summary>
internal sealed class LoopbackPeer : System.IDisposable
{
    private readonly Socket _socket;

    public LoopbackPeer()
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
    }

    public IPEndPoint EndPoint => (IPEndPoint)_socket.LocalEndPoint!;

    /// <summary>Send raw bytes to a destination endpoint.</summary>
    public void SendTo(byte[] data, IPEndPoint to) => _socket.SendTo(data, to);

    /// <summary>Block waiting for a datagram. Returns null on timeout.</summary>
    public byte[]? Receive(System.TimeSpan timeout)
    {
        _socket.ReceiveTimeout = (int)timeout.TotalMilliseconds;
        var buf = new byte[1500];
        try
        {
            var n = _socket.Receive(buf);
            return buf.AsSpan(0, n).ToArray();
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
        {
            return null;
        }
    }

    public void Dispose()
    {
        try { _socket.Shutdown(SocketShutdown.Both); } catch { }
        _socket.Dispose();
    }
}
