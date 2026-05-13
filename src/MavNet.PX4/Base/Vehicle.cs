using MavNet.Core;
using MavNet.Protocol;
using MavNet.PX4;
using MavNet.Transport.Udp;
using Microsoft.Extensions.Logging;
using MavNet.Protocol.Generated.Enums;
using MavNet.Protocol.Generated.Messages;

namespace MavNet.PX4.Base;

/// <summary>
/// Logical handle to one MAVLink vehicle: a typed view of its telemetry plus
/// async command helpers built on the canonical MAVLink command pattern
/// (COMMAND_LONG → race ACK against state-change HEARTBEAT, with timeout).
///
/// <para>Vehicle is a <em>consumer</em> of <see cref="MavlinkConnection"/> — it
/// subscribes to the connection's typed events in its constructor and filters by
/// the target <see cref="SystemId"/>/<see cref="ComponentId"/>. Multiple Vehicles
/// can in principle share one Connection (they each filter for their own sender),
/// though our factories create one Vehicle per Connection for simplicity.</para>
///
/// <para><b>Thread model.</b> State writes happen on the connection's receive
/// thread. Reads are eventually-consistent — for telemetry display this is fine.
/// <see cref="StateChanged"/> fires synchronously on the receive thread; consumers
/// must marshal to their UI thread themselves.</para>
///
/// <para><b>Metadata.</b> <see cref="Id"/> and <see cref="Name"/> are opt-in,
/// mutable, never read by the library — set them from your fleet/registry if you
/// want them to follow the vehicle around.</para>
/// </summary>
public abstract class Vehicle : IAsyncDisposable, IStateObservable
{
    private readonly MavlinkConnection _connection;
    private readonly bool _ownsConnection;
    private readonly TimeSpan _heartbeatTimeout;
    private readonly ILogger<Vehicle>? _log;

    // Throttled state subscriptions. Raw subscribers attach directly to StateChanged; throttled
    // ones live in _throttledSubs and share one Timer whose period tracks the FASTEST subscriber.
    // Each sub still honors its own MinInterval — the shared timer only sets the granularity at
    // which "is anyone due?" is checked. Allocated lazily so raw-only consumers pay nothing.
    private readonly object _subsLock = new();
    private List<ThrottledSub>? _throttledSubs;
    private Timer? _throttleTimer;
    private long _currentTimerPeriodTicks;

    private sealed class ThrottledSub
    {
        public required Action Handler { get; init; }
        public required long MinIntervalTicks { get; init; }
        public long LastFiredTicks;
        public int Dirty;
    }

    // Heartbeat-derived state.
    private volatile bool _armed;
    private string _mode = "—";
    private long _lastHbTicks;
    private PendingCommand? _pendingCommand;

    // Telemetry state — written from the receive thread, read from anywhere.
    private double _lat, _lon, _alt, _hdg, _vel;
    private int _sats;
    private GpsFixType _gpsFix = GpsFixType.NoGps;
    private double _battery;
    private MavLandedState _landedState = MavLandedState.Undefined;

    /// <summary>MAVLink system id of this vehicle (e.g. 1 for a typical autopilot).</summary>
    public byte SystemId { get; }

    /// <summary>MAVLink component id of this vehicle's autopilot (typically 1).</summary>
    public byte ComponentId { get; }

    /// <summary>Vehicle category from the latest HEARTBEAT (quadrotor, plane, …).</summary>
    public MavType VehicleType { get; protected internal set; }

    /// <summary>Connection string the underlying transport was opened with, or null if unknown.</summary>
    public string? ConnectionString { get; init; }

    /// <summary>Pretty identifier: <c>sys{SystemId}-comp{ComponentId}</c>.</summary>
    public string DeviceId => $"sys{SystemId}-comp{ComponentId}";

    /// <summary>Opt-in application-assigned identifier. The library never reads this — set it from your fleet/registry.</summary>
    public string? Id { get; set; }

    /// <summary>Opt-in display name. The library never reads this — set it from your fleet/registry.</summary>
    public string? Name { get; set; }

    /// <summary>Armed bit from the latest HEARTBEAT's base_mode.</summary>
    public bool Armed => _armed;

    /// <summary>Human-readable PX4 mode label (e.g. <c>AUTO.LOITER</c>) parsed from custom_mode.</summary>
    public string Mode => Volatile.Read(ref _mode);

    /// <summary>True iff a HEARTBEAT was received within the heartbeat-timeout window.</summary>
    public bool LinkUp
    {
        get
        {
            var last = Interlocked.Read(ref _lastHbTicks);
            if (last == 0) return false;
            return (DateTime.UtcNow.Ticks - last) < _heartbeatTimeout.Ticks;
        }
    }

    public DateTime LastHeartbeatAt => new(Interlocked.Read(ref _lastHbTicks), DateTimeKind.Utc);

    public double Lat => _lat;
    public double Lon => _lon;
    public double Alt => _alt;
    public double Hdg => _hdg;
    public double Vel => _vel;
    public int Sats => _sats;
    public GpsFixType GpsFix => _gpsFix;
    public double Battery => _battery;
    public MavLandedState LandedState => _landedState;

    /// <summary>
    /// Fires after every state-affecting message — high frequency (10–50 Hz typical). Prefer
    /// <see cref="SubscribeState(Action, StateRate)"/>: it lets you pick a throttle rate at
    /// the call site (<c>StateRate.Hz(2)</c>, <c>StateRate.Every(...)</c>, or <c>StateRate.Raw</c>
    /// for full fidelity). This raw event remains public for consumers that genuinely want
    /// every packet without going through the throttler.
    /// </summary>
    public event Action? StateChanged;

    /// <summary>Diagnostic: fires once per inbound HEARTBEAT with the decoded payload and wall-clock receive timestamp.</summary>
    public event Action<Heartbeat, DateTime>? HeartbeatReceived;

    /// <param name="connection">The transport. Subscribes to its typed events.</param>
    /// <param name="targetSystemId">Sender system id this Vehicle accepts messages from.</param>
    /// <param name="targetComponentId">Sender component id this Vehicle accepts messages from.</param>
    /// <param name="ownsConnection">When true, <see cref="DisposeAsync"/> also disposes the connection. Use false when sharing a connection across multiple Vehicles.</param>
    /// <param name="heartbeatTimeout">Window in which a heartbeat must arrive for <see cref="LinkUp"/> to stay true.</param>
    /// <param name="logger">Optional. Null = no logging.</param>
    protected Vehicle(
        MavlinkConnection connection,
        byte targetSystemId,
        byte targetComponentId,
        bool ownsConnection = false,
        TimeSpan? heartbeatTimeout = null,
        ILogger<Vehicle>? logger = null)
    {
        _connection = connection;
        _ownsConnection = ownsConnection;
        SystemId = targetSystemId;
        ComponentId = targetComponentId;
        _heartbeatTimeout = heartbeatTimeout ?? TimeSpan.FromSeconds(5);
        _log = logger;

        _connection.HeartbeatReceived         += OnHeartbeat;
        _connection.CommandAckReceived        += OnCommandAck;
        _connection.GlobalPositionIntReceived += OnGlobalPosition;
        _connection.VfrHudReceived            += OnVfrHud;
        _connection.GpsRawIntReceived         += OnGpsRaw;
        _connection.SysStatusReceived         += OnSysStatus;
        _connection.ExtendedSysStateReceived  += OnExtendedSysState;
    }

    private bool IsMine(MavId sender) =>
        sender.SystemId == SystemId && sender.ComponentId == ComponentId;

    private void OnHeartbeat(MavId sender, Heartbeat hb, DateTime at)
    {
        if (!IsMine(sender)) return;

        _armed = (hb.BaseMode & MavModeFlag.SafetyArmed) != 0;
        Volatile.Write(ref _mode, Px4Mode.Format(hb.CustomMode));
        VehicleType = hb.Type;
        Interlocked.Exchange(ref _lastHbTicks, at.Ticks);

        try { HeartbeatReceived?.Invoke(hb, at); } catch { }
        FireStateChanged();

        // Confirm any pending command via state change.
        var pending = _pendingCommand;
        if (pending is not null && pending.StateMatcher?.Invoke(hb) == true)
            pending.TryComplete(CommandResult.Confirmed,
                pending.AckAccepted ? MavResult.Accepted : null);
    }

    private void OnCommandAck(MavId sender, CommandAck ack, DateTime at)
    {
        if (!IsMine(sender)) return;
        var pending = _pendingCommand;
        if (pending is null || pending.Command != ack.Command) return;

        if (ack.Result == MavResult.Accepted)
            pending.AckAccepted = true;                 // wait for state-change confirmation
        else if (ack.Result != MavResult.InProgress)
            pending.TryComplete(CommandResult.Rejected, ack.Result);
    }

    private void OnGlobalPosition(MavId sender, GlobalPositionInt p, DateTime at)
    {
        if (!IsMine(sender)) return;
        _lat = p.Lat / 1e7;
        _lon = p.Lon / 1e7;
        _alt = p.RelativeAlt / 1000.0;
        _hdg = p.Hdg == ushort.MaxValue ? 0 : p.Hdg / 100.0;
        var vx = p.Vx / 100.0;
        var vy = p.Vy / 100.0;
        _vel = Math.Sqrt(vx * vx + vy * vy);
        FireStateChanged();
    }

    private void OnVfrHud(MavId sender, VfrHud v, DateTime at)
    {
        if (!IsMine(sender)) return;
        if (_vel == 0) _vel = v.Groundspeed;
        FireStateChanged();
    }

    private void OnGpsRaw(MavId sender, GpsRawInt g, DateTime at)
    {
        if (!IsMine(sender)) return;
        _sats = g.SatellitesVisible;
        _gpsFix = g.FixType;
        FireStateChanged();
    }

    private void OnSysStatus(MavId sender, SysStatus s, DateTime at)
    {
        if (!IsMine(sender)) return;
        _battery = s.BatteryRemaining < 0 ? 0 : s.BatteryRemaining;
        FireStateChanged();
    }

    private void OnExtendedSysState(MavId sender, ExtendedSysState e, DateTime at)
    {
        if (!IsMine(sender)) return;
        _landedState = e.LandedState;
        FireStateChanged();
    }

    private void FireStateChanged()
    {
        try { StateChanged?.Invoke(); } catch { /* never break the receive loop */ }

        // Mark throttled subscribers dirty. Snapshot the list outside the per-handler call to
        // keep the lock window tiny; the timer reads the same list.
        if (_throttledSubs is null) return;
        lock (_subsLock)
        {
            if (_throttledSubs is null) return;
            for (int i = 0; i < _throttledSubs.Count; i++)
                Interlocked.Exchange(ref _throttledSubs[i].Dirty, 1);
        }
    }

    /// <summary>
    /// Subscribe to state-change notifications at the given rate. The handler may run on
    /// any thread — for Blazor consumers, marshal to the UI thread via <c>InvokeAsync</c>
    /// inside the handler.
    /// </summary>
    /// <param name="handler">Called on each delivery. Exceptions are swallowed to keep the throttler alive.</param>
    /// <param name="rate"><see cref="StateRate.Raw"/> = every packet; otherwise rate-limited.</param>
    /// <returns>Dispose to unsubscribe. Idempotent.</returns>
    public StateSubscription SubscribeState(Action handler, StateRate rate)
    {
        ArgumentNullException.ThrowIfNull(handler);

        if (rate.IsRaw)
        {
            StateChanged += handler;
            return new StateSubscription(rate, () => StateChanged -= handler);
        }

        var sub = new ThrottledSub { Handler = handler, MinIntervalTicks = rate.MinInterval.Ticks };
        lock (_subsLock)
        {
            _throttledSubs ??= new List<ThrottledSub>();
            _throttledSubs.Add(sub);
            RetargetTimerLocked();
        }
        return new StateSubscription(rate, () =>
        {
            lock (_subsLock)
            {
                if (_throttledSubs is null) return;
                _throttledSubs.Remove(sub);
                if (_throttledSubs.Count == 0)
                {
                    _throttleTimer?.Dispose();
                    _throttleTimer = null;
                    _throttledSubs = null;
                    _currentTimerPeriodTicks = 0;
                }
                else
                {
                    RetargetTimerLocked();
                }
            }
        });
    }

    /// <summary>
    /// Recomputes the shared throttle timer's period to match the fastest active subscriber and
    /// retargets the timer if it changed. Must be called under <see cref="_subsLock"/>.
    /// </summary>
    private void RetargetTimerLocked()
    {
        if (_throttledSubs is null || _throttledSubs.Count == 0) return;

        long fastestTicks = long.MaxValue;
        foreach (var s in _throttledSubs)
            if (s.MinIntervalTicks < fastestTicks) fastestTicks = s.MinIntervalTicks;

        if (fastestTicks == _currentTimerPeriodTicks) return;
        _currentTimerPeriodTicks = fastestTicks;

        var period = TimeSpan.FromTicks(fastestTicks);
        if (_throttleTimer is null)
            _throttleTimer = new Timer(_ => OnThrottleTick(), null, period, period);
        else
            _throttleTimer.Change(period, period);
    }

    private void OnThrottleTick()
    {
        ThrottledSub[] snapshot;
        lock (_subsLock)
        {
            if (_throttledSubs is null || _throttledSubs.Count == 0) return;
            snapshot = _throttledSubs.ToArray();
        }

        var nowTicks = DateTime.UtcNow.Ticks;
        foreach (var sub in snapshot)
        {
            // Only fire if dirty AND the min interval has elapsed since the last delivery.
            if (Interlocked.CompareExchange(ref sub.Dirty, 0, 1) != 1) continue;
            if (nowTicks - Interlocked.Read(ref sub.LastFiredTicks) < sub.MinIntervalTicks)
            {
                // Too soon — put it back in the dirty queue for a later tick.
                Interlocked.Exchange(ref sub.Dirty, 1);
                continue;
            }
            Interlocked.Exchange(ref sub.LastFiredTicks, nowTicks);
            try { sub.Handler(); } catch { /* never throw from throttler */ }
        }
    }

    // ----- Commands ---------------------------------------------------------

    public Task<CommandOutcome> ArmAsync(CancellationToken ct = default) =>
        ExecuteCommandAsync(
            BuildCommandLong(MavCmd.ComponentArmDisarm, p1: 1f),
            hb => (hb.BaseMode & MavModeFlag.SafetyArmed) != 0,
            ct);

    public Task<CommandOutcome> DisarmAsync(CancellationToken ct = default) =>
        ExecuteCommandAsync(
            BuildCommandLong(MavCmd.ComponentArmDisarm, p1: 0f),
            hb => (hb.BaseMode & MavModeFlag.SafetyArmed) == 0,
            ct);

    public Task<CommandOutcome> ReturnToLaunchAsync(CancellationToken ct = default) =>
        ExecuteCommandAsync(
            BuildCommandLong(MavCmd.NavReturnToLaunch),
            hb => Px4Mode.Format(hb.CustomMode).StartsWith("AUTO.RTL", StringComparison.Ordinal),
            ct);

    public virtual Task<CommandOutcome> TakeoffAsync(double altMeters, CancellationToken ct = default) =>
        ExecuteCommandAsync(
            BuildCommandLong(MavCmd.NavTakeoff,
                p1: 0f,
                p4: float.NaN, p5: float.NaN, p6: float.NaN,
                p7: (float)altMeters),
            hb => (hb.BaseMode & MavModeFlag.SafetyArmed) != 0
                  || Px4Mode.Format(hb.CustomMode).StartsWith("AUTO.TAKEOFF", StringComparison.Ordinal),
            ct);

    public virtual Task<CommandOutcome> LandAsync(CancellationToken ct = default) =>
        ExecuteCommandAsync(
            BuildCommandLong(MavCmd.NavLand,
                p4: float.NaN, p5: float.NaN, p6: float.NaN),
            hb => Px4Mode.Format(hb.CustomMode).StartsWith("AUTO.LAND", StringComparison.Ordinal),
            ct);

    /// <summary>Send an arbitrary message through this Vehicle's underlying connection. Useful for custom commands that don't have a typed wrapper.</summary>
    protected void Send<T>(T message) where T : IMavlinkMessage<T> => _connection.Send(message);

    private CommandLong BuildCommandLong(MavCmd cmd,
        float p1 = 0f, float p2 = 0f, float p3 = 0f, float p4 = 0f,
        float p5 = 0f, float p6 = 0f, float p7 = 0f) =>
        new CommandLong(
            TargetSystem: SystemId,
            TargetComponent: ComponentId,
            Command: cmd,
            Confirmation: 0,
            Param1: p1, Param2: p2, Param3: p3, Param4: p4,
            Param5: p5, Param6: p6, Param7: p7);

    private async Task<CommandOutcome> ExecuteCommandAsync(
        CommandLong packet,
        Func<Heartbeat, bool>? stateMatcher,
        CancellationToken ct)
    {
        var pending = new PendingCommand(packet.Command, stateMatcher);

        var prior = Interlocked.Exchange(ref _pendingCommand, pending);
        prior?.TryComplete(CommandResult.Timeout, null);

        _connection.Send(packet);

        using var timeoutCts = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromSeconds(6), timeoutCts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            var result = pending.AckAccepted ? CommandResult.Sent : CommandResult.Timeout;
            var ack    = pending.AckAccepted ? MavResult.Accepted : (MavResult?)null;
            pending.TryComplete(result, ack);
        }, TaskScheduler.Default);

        var ctReg = ct.Register(() => pending.TryComplete(CommandResult.Timeout, null));
        try
        {
            return await pending.Tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            ctReg.Dispose();
            timeoutCts.Cancel();
            Interlocked.CompareExchange(ref _pendingCommand, null, pending);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _connection.HeartbeatReceived         -= OnHeartbeat;
        _connection.CommandAckReceived        -= OnCommandAck;
        _connection.GlobalPositionIntReceived -= OnGlobalPosition;
        _connection.VfrHudReceived            -= OnVfrHud;
        _connection.GpsRawIntReceived         -= OnGpsRaw;
        _connection.SysStatusReceived         -= OnSysStatus;
        _connection.ExtendedSysStateReceived  -= OnExtendedSysState;

        lock (_subsLock)
        {
            _throttleTimer?.Dispose();
            _throttleTimer = null;
            _throttledSubs = null;
        }

        if (_ownsConnection)
            await _connection.DisposeAsync().ConfigureAwait(false);
    }
}
