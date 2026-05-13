using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using MavNet.Core;
using MavNet.Protocol;
using Microsoft.Extensions.Logging;
using MavNet.Protocol.Generated.Enums;
using MavNet.Protocol.Generated.Messages;

namespace MavNet.Transport.Udp;

/// <summary>
/// A MAVLink v2 UDP transport. Owns one socket, runs one receive loop, and
/// broadcasts a GCS heartbeat at a fixed rate. Exposes inbound MAVLink as
/// strongly-typed events keyed on the sender's <see cref="MavId"/>.
///
/// <para><b>Thread model.</b> Inbound events fire <strong>on the receive thread</strong>,
/// synchronously. Consumers that need to marshal to a UI thread (e.g. Blazor) must
/// do so themselves (<c>InvokeAsync(StateHasChanged)</c>). Subscriber exceptions are
/// caught — a buggy subscriber cannot break the receive loop.</para>
///
/// <para><b>Sending.</b> Use <see cref="Send{T}(T)"/> with any generated MAVLink
/// message. The connection knows the message's <see cref="IMavlinkMessage{TSelf}.MsgId"/>,
/// <see cref="IMavlinkMessage{TSelf}.CrcExtra"/>, and <see cref="IMavlinkMessage{TSelf}.MaxPayloadLength"/>
/// at the call site — no runtime lookup.</para>
///
/// <para><b>Lifetime.</b> Implements <see cref="IAsyncDisposable"/>; safe to dispose
/// multiple times. Disposing closes the socket and stops the receive loop.</para>
/// </summary>
public sealed class MavlinkConnection : IMavlinkConnection
{
    private readonly Socket _socket;
    private readonly EndPoint _remote;
    private readonly byte _selfSystemId;
    private readonly byte _selfComponentId;
    private readonly int _gcsHeartbeatIntervalMs;
    private readonly ILogger<MavlinkConnection>? _log;
    private readonly CancellationTokenSource _cts = new();
    private Timer? _heartbeatTimer;
    private Task? _receiveLoop;
    private int _sequence;
    private bool _started;
    private bool _disposed;

    /// <summary>Fires for every inbound HEARTBEAT. Args: sender, decoded message, receive timestamp.</summary>
    public event Action<MavId, Heartbeat, DateTime>?         HeartbeatReceived;
    /// <summary>Fires for every inbound COMMAND_ACK.</summary>
    public event Action<MavId, CommandAck, DateTime>?        CommandAckReceived;
    /// <summary>Fires for every inbound GLOBAL_POSITION_INT.</summary>
    public event Action<MavId, GlobalPositionInt, DateTime>? GlobalPositionIntReceived;
    /// <summary>Fires for every inbound VFR_HUD.</summary>
    public event Action<MavId, VfrHud, DateTime>?            VfrHudReceived;
    /// <summary>Fires for every inbound GPS_RAW_INT.</summary>
    public event Action<MavId, GpsRawInt, DateTime>?         GpsRawIntReceived;
    /// <summary>Fires for every inbound SYS_STATUS.</summary>
    public event Action<MavId, SysStatus, DateTime>?         SysStatusReceived;
    /// <summary>Fires for every inbound EXTENDED_SYS_STATE.</summary>
    public event Action<MavId, ExtendedSysState, DateTime>?  ExtendedSysStateReceived;

    /// <summary>Creates and binds the UDP socket. Call <see cref="Start"/> to begin receiving.</summary>
    public MavlinkConnection(
        IPEndPoint localBind,
        IPEndPoint remote,
        byte selfSystemId = 255,
        byte selfComponentId = 190,
        int gcsHeartbeatIntervalMs = 100,
        ILogger<MavlinkConnection>? logger = null)
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(localBind);
        _remote = remote;
        _selfSystemId = selfSystemId;
        _selfComponentId = selfComponentId;
        _gcsHeartbeatIntervalMs = gcsHeartbeatIntervalMs;
        _log = logger;
    }

    /// <summary>The local <see cref="IPEndPoint"/> the UDP socket is bound to. When the caller
    /// passed port 0 to the constructor, this is the OS-assigned ephemeral port.</summary>
    public IPEndPoint? LocalEndPoint => _socket.LocalEndPoint as IPEndPoint;

    /// <summary>Begin reading from the socket and emitting events. Idempotent within one instance — throws if called twice.</summary>
    public void Start()
    {
        if (_started) throw new InvalidOperationException("Already started.");
        _started = true;
        _receiveLoop = Task.Run(ReceiveLoopAsync);
        _heartbeatTimer = new Timer(_ => SendGcsHeartbeat(), null, 0, _gcsHeartbeatIntervalMs);
        _log?.LogInformation("MavlinkConnection started: local={Local} remote={Remote} selfSys={Sys} selfComp={Comp}",
            ((IPEndPoint?)_socket.LocalEndPoint)?.ToString() ?? "?", _remote, _selfSystemId, _selfComponentId);
    }

    /// <summary>Send a generated MAVLink message. Frame is built and CRC-stamped on this thread; the actual UDP send is non-blocking.</summary>
    public void Send<T>(T message) where T : IMavlinkMessage<T>
    {
        Span<byte> payload = stackalloc byte[T.MaxPayloadLength];
        message.Encode(payload);
        // MAVLink v2 wire truncation: strip trailing zero bytes down to MinPayloadLength.
        var wireLen = payload.Length;
        while (wireLen > T.MinPayloadLength && payload[wireLen - 1] == 0) wireLen--;
        SendRaw(T.MsgId, T.CrcExtra, payload[..wireLen]);
    }

    /// <summary>Send raw bytes as a MAVLink v2 frame. Use <see cref="Send{T}(T)"/> for known message types — this is the escape hatch.</summary>
    public void SendRaw(uint msgId, byte crcExtra, ReadOnlySpan<byte> payload)
    {
        Span<byte> frame = stackalloc byte[12 + payload.Length];
        frame[0] = 0xFD;
        frame[1] = (byte)payload.Length;
        frame[2] = 0;
        frame[3] = 0;
        frame[4] = (byte)(Interlocked.Increment(ref _sequence) & 0xFF);
        frame[5] = _selfSystemId;
        frame[6] = _selfComponentId;
        frame[7] = (byte)(msgId & 0xFF);
        frame[8] = (byte)((msgId >> 8) & 0xFF);
        frame[9] = (byte)((msgId >> 16) & 0xFF);
        payload.CopyTo(frame[10..]);

        var crc = Crc16.Accumulate(frame[1..(10 + payload.Length)]);
        crc = Crc16.Accumulate(crcExtra, crc);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.Slice(10 + payload.Length, 2), crc);

        _socket.SendTo(frame, _remote);
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[1500];
        EndPoint fromAny = new IPEndPoint(IPAddress.Any, 0);
        var token = _cts.Token;

        while (!token.IsCancellationRequested)
        {
            try
            {
                var result = await _socket
                    .ReceiveFromAsync(buffer, SocketFlags.None, fromAny, token)
                    .ConfigureAwait(false);
                // Capture timestamp before decode — this is the latency baseline.
                var receivedAt = DateTime.UtcNow;
                Dispatch(buffer.AsSpan(0, result.ReceivedBytes), receivedAt);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException)   { break; }
            catch (SocketException ex)
            {
                // WSAECONNRESET (10054) on Windows: harmless after a send to a port that's gone.
                _log?.LogDebug(ex, "Socket exception on receive (continuing)");
            }
        }
    }

    private void Dispatch(ReadOnlySpan<byte> data, DateTime receivedAt)
    {
        if (!MavlinkFrame.TryDecode(data, out var frame)) return;

        var sender = new MavId(frame.SystemId, frame.ComponentId);
        switch (frame.MessageId)
        {
            case Heartbeat.MsgId:
                Fire(HeartbeatReceived, sender, Heartbeat.Decode(frame.Payload), receivedAt);
                break;
            case CommandAck.MsgId:
                Fire(CommandAckReceived, sender, CommandAck.Decode(frame.Payload), receivedAt);
                break;
            case GlobalPositionInt.MsgId:
                Fire(GlobalPositionIntReceived, sender, GlobalPositionInt.Decode(frame.Payload), receivedAt);
                break;
            case VfrHud.MsgId:
                Fire(VfrHudReceived, sender, VfrHud.Decode(frame.Payload), receivedAt);
                break;
            case GpsRawInt.MsgId:
                Fire(GpsRawIntReceived, sender, GpsRawInt.Decode(frame.Payload), receivedAt);
                break;
            case SysStatus.MsgId:
                Fire(SysStatusReceived, sender, SysStatus.Decode(frame.Payload), receivedAt);
                break;
            case ExtendedSysState.MsgId:
                Fire(ExtendedSysStateReceived, sender, ExtendedSysState.Decode(frame.Payload), receivedAt);
                break;
                // Other message ids are silently ignored — extend the dispatcher when you add msgs to the allowlist.
        }
    }

    private static void Fire<T>(Action<MavId, T, DateTime>? handler, MavId sender, T msg, DateTime at)
    {
        if (handler is null) return;
        try { handler(sender, msg, at); }
        catch { /* subscriber bug must never break the receive loop */ }
    }

    private void SendGcsHeartbeat()
    {
        // Standard GCS heartbeat: type=GCS, autopilot=INVALID, base_mode=CUSTOM_MODE_ENABLED,
        // system_status=ACTIVE. PX4 accepts this as a valid GCS partner.
        var hb = new Heartbeat(
            CustomMode: 0,
            Type: MavType.Gcs,
            Autopilot: MavAutopilot.Invalid,
            BaseMode: MavModeFlag.CustomModeEnabled,
            SystemStatus: MavState.Active,
            MavlinkVersion: 3);
        try { Send(hb); }
        catch (Exception ex) { _log?.LogDebug(ex, "GCS heartbeat send failed (retrying next tick)"); }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        if (_heartbeatTimer is not null) await _heartbeatTimer.DisposeAsync().ConfigureAwait(false);
        try { _socket.Shutdown(SocketShutdown.Both); } catch { }
        _socket.Dispose();
        if (_receiveLoop is not null)
        {
            try { await _receiveLoop.ConfigureAwait(false); } catch { }
        }
        _cts.Dispose();
        _log?.LogInformation("MavlinkConnection disposed");
    }
}
