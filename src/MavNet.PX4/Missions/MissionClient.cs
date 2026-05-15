using MavNet.Core;
using MavNet.Protocol.Generated.Enums;
using MavNet.Protocol.Generated.Messages;
using MavNet.Transport.Udp;
using Microsoft.Extensions.Logging;

namespace MavNet.PX4.Missions;

/// <summary>
/// Implements the MAVLink mission-protocol upload and download state machines for
/// one <c>(target, <see cref="MavMissionType"/>)</c> pair on top of an
/// <see cref="IMavlinkConnection"/>. One <see cref="MissionClient"/> handles one
/// mission type (waypoints, fence, or rally) — Vehicle owns one per type so the
/// three transactions can run independently per spec.
///
/// <para><b>Upload.</b> Caller invokes <see cref="UploadAsync"/>. We send
/// <c>MISSION_COUNT</c>, then for each <c>MISSION_REQUEST_INT</c> (or its
/// deprecated <c>MISSION_REQUEST</c> sibling that ArduPilot still emits) we reply
/// with a <c>MISSION_ITEM_INT</c>. After the final item the vehicle answers with
/// <c>MISSION_ACK</c>, which terminates the transaction.</para>
///
/// <para><b>Download.</b> Caller invokes <see cref="DownloadAsync"/>. We send
/// <c>MISSION_REQUEST_LIST</c>, the vehicle responds with <c>MISSION_COUNT</c>
/// (carrying the on-vehicle <c>opaque_id</c>), then we send a
/// <c>MISSION_REQUEST_INT</c> per item in order and collect each
/// <c>MISSION_ITEM_INT</c> reply. After the final item we send a final
/// <c>MISSION_ACK(MAV_MISSION_ACCEPTED)</c> per spec to close the transaction.</para>
///
/// <para><b>Timing.</b> Per the MAVLink spec: 1500 ms default timeout, 250 ms for
/// the inner item loop, max 5 retries. Defaults are baked in but all three are
/// constructor-injectable so tests stay sub-second.</para>
///
/// <para><b>Out-of-sequence is not an error.</b> If the vehicle re-requests an
/// earlier seq during upload, we re-answer with that item rather than abort. If
/// the vehicle answers with an unexpected seq during download, we just ignore it
/// and let the watchdog re-request. Per spec only timeout exhaustion and a
/// non-Accepted <c>MISSION_ACK</c> cause a transaction to fail.</para>
///
/// <para><b>Threading.</b> Inbound events fire on the connection's receive thread.
/// All mutable state is guarded by an internal lock; the lock window only covers
/// the state mutation, never the awaiter's continuation (the result <c>Task</c>
/// uses <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>).</para>
///
/// <para><b>Single-flight.</b> Only one transaction may be in flight per
/// <see cref="MissionClient"/>, regardless of direction. A concurrent
/// <see cref="UploadAsync"/> or <see cref="DownloadAsync"/> throws
/// <see cref="InvalidOperationException"/>.</para>
/// </summary>
public sealed class MissionClient : IDisposable
{
    private readonly IMavlinkConnection _connection;
    private readonly byte _targetSystem;
    private readonly byte _targetComponent;
    private readonly MavMissionType _missionType;
    private readonly TimeSpan _defaultTimeout;
    private readonly TimeSpan _itemTimeout;
    private readonly int _maxRetries;
    private readonly ILogger? _log;

    private readonly object _lock = new();
    private Transaction? _tx;
    private int _disposed;

    /// <summary>Common base for one in-flight transaction. Each direction (upload, download)
    /// supplies its own <see cref="Complete"/> implementation to resolve the right typed result.</summary>
    private abstract class Transaction
    {
        public required DateTime StartedAt { get; init; }
        public CancellationTokenRegistration CtReg;
        public CancellationTokenSource? Watchdog;
        public abstract void Complete(MissionTransactionStatus status, MavMissionResult? ackResult, uint? opaqueId, TimeSpan elapsed);
    }

    private sealed class UploadTransaction : Transaction
    {
        public required TaskCompletionSource<MissionUploadResult> Tcs { get; init; }
        public required IReadOnlyList<MissionItem> Items { get; init; }

        public override void Complete(MissionTransactionStatus status, MavMissionResult? ackResult, uint? opaqueId, TimeSpan elapsed)
            => Tcs.TrySetResult(new MissionUploadResult(status, ackResult, opaqueId, elapsed));
    }

    private sealed class ClearTransaction : Transaction
    {
        public required TaskCompletionSource<MissionClearResult> Tcs { get; init; }

        public override void Complete(MissionTransactionStatus status, MavMissionResult? ackResult, uint? opaqueId, TimeSpan elapsed)
            => Tcs.TrySetResult(new MissionClearResult(status, ackResult, elapsed));
    }

    private sealed class DownloadTransaction : Transaction
    {
        public required TaskCompletionSource<MissionDownloadResult> Tcs { get; init; }
        // ExpectedCount is set when MISSION_COUNT arrives from the vehicle.
        public ushort ExpectedCount;
        public uint OpaqueId;
        public MissionItem[]? Items;
        // The seq we are currently requesting; advanced as items arrive.
        public ushort NextSeq;

        public override void Complete(MissionTransactionStatus status, MavMissionResult? ackResult, uint? opaqueId, TimeSpan elapsed)
        {
            IReadOnlyList<MissionItem> items =
                status == MissionTransactionStatus.Accepted && Items is not null
                    ? Items
                    : Array.Empty<MissionItem>();
            Tcs.TrySetResult(new MissionDownloadResult(status, items, opaqueId ?? OpaqueId, elapsed));
        }
    }

    /// <summary>Default per-message timeout (1500 ms per spec unless overridden).</summary>
    public TimeSpan DefaultTimeout => _defaultTimeout;

    /// <summary>Item-request timeout (250 ms per spec unless overridden) — used for the
    /// inner request/response loop after the first <c>MISSION_REQUEST_INT</c> arrives.</summary>
    public TimeSpan ItemTimeout => _itemTimeout;

    /// <summary>Max retries per message (5 per spec unless overridden).</summary>
    public int MaxRetries => _maxRetries;

    /// <summary>The mission type this client handles.</summary>
    public MavMissionType MissionType => _missionType;

    /// <summary>True iff a transaction (upload, download, …) is currently in flight.</summary>
    public bool IsBusy { get { lock (_lock) return _tx is not null; } }

    /// <summary>Create a client bound to one target and one mission type.</summary>
    /// <param name="connection">Transport — subscribes to its mission events for the lifetime of this client.</param>
    /// <param name="targetSystem">Sender system id we accept inbound mission messages from, and the target on outbound.</param>
    /// <param name="targetComponent">Sender component id we accept inbound mission messages from, and the target on outbound.</param>
    /// <param name="missionType">Which on-vehicle plan this client manages.</param>
    /// <param name="defaultTimeout">Per-message timeout. Defaults to 1500 ms (spec).</param>
    /// <param name="itemTimeout">Inner item-loop timeout. Defaults to 250 ms (spec).</param>
    /// <param name="maxRetries">Max retries per message before failing as <see cref="MissionTransactionStatus.Timeout"/>. Defaults to 5 (spec).</param>
    /// <param name="logger">Optional. Null = no logging.</param>
    public MissionClient(
        IMavlinkConnection connection,
        byte targetSystem,
        byte targetComponent,
        MavMissionType missionType = MavMissionType.Mission,
        TimeSpan? defaultTimeout = null,
        TimeSpan? itemTimeout = null,
        int maxRetries = 5,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (maxRetries < 0)
            throw new ArgumentOutOfRangeException(nameof(maxRetries), "Must be non-negative.");

        _connection = connection;
        _targetSystem = targetSystem;
        _targetComponent = targetComponent;
        _missionType = missionType;
        _defaultTimeout = defaultTimeout ?? TimeSpan.FromMilliseconds(1500);
        _itemTimeout = itemTimeout ?? TimeSpan.FromMilliseconds(250);
        _maxRetries = maxRetries;
        _log = logger;

        _connection.MissionRequestIntReceived += OnRequestInt;
        _connection.MissionRequestReceived    += OnRequest;
        _connection.MissionAckReceived        += OnAck;
        _connection.MissionCountReceived      += OnCount;
        _connection.MissionItemIntReceived    += OnItemInt;
    }

    // ----- Upload -----------------------------------------------------------

    /// <summary>
    /// Upload <paramref name="items"/> to the target vehicle for this client's
    /// <see cref="MissionType"/>. Resolves when the vehicle responds with
    /// <c>MISSION_ACK</c>, after retry/timeout budget exhaustion, on cancellation,
    /// or on disposal.
    /// </summary>
    /// <param name="items">Items in order. <c>Count</c> must fit in a <see cref="ushort"/>.
    /// An empty list is accepted (the vehicle treats <c>MISSION_COUNT=0</c> as a clear).</param>
    /// <param name="ct">Cancellation. On firing, the transaction completes with
    /// <see cref="MissionTransactionStatus.Cancelled"/>; the vehicle may be left in a
    /// partial-upload state and a follow-up clear/re-upload is advisable.</param>
    /// <exception cref="InvalidOperationException">Another transaction is already in flight.</exception>
    /// <exception cref="ObjectDisposedException">This client has been disposed.</exception>
    /// <exception cref="ArgumentNullException">If <paramref name="items"/> is null.</exception>
    /// <exception cref="ArgumentException">If <paramref name="items"/> exceeds <c>ushort.MaxValue</c>.</exception>
    public Task<MissionUploadResult> UploadAsync(IReadOnlyList<MissionItem> items, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count > ushort.MaxValue)
            throw new ArgumentException($"Mission too large ({items.Count} items, max {ushort.MaxValue}).", nameof(items));
        ThrowIfDisposed();

        UploadTransaction tx;
        lock (_lock)
        {
            ThrowIfBusyLocked();
            tx = new UploadTransaction
            {
                Tcs = new TaskCompletionSource<MissionUploadResult>(TaskCreationOptions.RunContinuationsAsynchronously),
                Items = items,
                StartedAt = DateTime.UtcNow,
            };
            _tx = tx;

            SendCount((ushort)items.Count);
            ArmWatchdogLocked(tx, _defaultTimeout, _maxRetries, ResendUploadInitial);
        }

        tx.CtReg = ct.Register(() => CompleteTransaction(MissionTransactionStatus.Cancelled, ackResult: null, opaqueId: null));
        return tx.Tcs.Task;

        void ResendUploadInitial() => SendCount((ushort)items.Count);
    }

    private void OnRequestInt(MavId sender, MissionRequestInt req, DateTime _)
    {
        if (!IsMine(sender) || req.MissionType != _missionType) return;
        HandleUploadRequest(req.Seq);
    }

    private void OnRequest(MavId sender, MissionRequest req, DateTime _)
    {
        // Deprecated MISSION_REQUEST (msgid 40) — ArduPilot still sends it. Per spec we
        // respond with a MISSION_ITEM_INT identical to the MISSION_REQUEST_INT case.
        if (!IsMine(sender) || req.MissionType != _missionType) return;
        HandleUploadRequest(req.Seq);
    }

    private void HandleUploadRequest(ushort seq)
    {
        lock (_lock)
        {
            if (_tx is not UploadTransaction tx) return;
            if (seq >= tx.Items.Count)
            {
                // Out-of-range request — not an error per spec ("Out-of-sequence messages
                // in mission upload/download are recoverable, and are not treated as errors").
                // Ignore; the next watchdog tick will resend the last item we sent.
                _log?.LogDebug("Mission {Type}: out-of-range upload request seq={Seq} (count={Count}), ignoring",
                    _missionType, seq, tx.Items.Count);
                return;
            }

            SendItem(tx.Items[seq], seq);
            ArmWatchdogLocked(tx, _itemTimeout, _maxRetries, () => SendItem(tx.Items[seq], seq));
        }
    }

    // ----- Clear ------------------------------------------------------------

    /// <summary>
    /// Clear the on-vehicle plan for this client's <see cref="MissionType"/>.
    /// Sends <c>MISSION_CLEAR_ALL</c> and awaits <c>MISSION_ACK</c>. Resolves when
    /// the vehicle ACKs, after retry/timeout exhaustion, on cancellation, or on disposal.
    /// </summary>
    /// <param name="ct">Cancellation. On firing, completes <see cref="MissionTransactionStatus.Cancelled"/>.</param>
    /// <exception cref="InvalidOperationException">Another transaction is already in flight.</exception>
    /// <exception cref="ObjectDisposedException">This client has been disposed.</exception>
    public Task<MissionClearResult> ClearAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        ClearTransaction tx;
        lock (_lock)
        {
            ThrowIfBusyLocked();
            tx = new ClearTransaction
            {
                Tcs = new TaskCompletionSource<MissionClearResult>(TaskCreationOptions.RunContinuationsAsynchronously),
                StartedAt = DateTime.UtcNow,
            };
            _tx = tx;

            SendClearAll();
            ArmWatchdogLocked(tx, _defaultTimeout, _maxRetries, SendClearAll);
        }

        tx.CtReg = ct.Register(() => CompleteTransaction(MissionTransactionStatus.Cancelled, ackResult: null, opaqueId: null));
        return tx.Tcs.Task;
    }

    // ----- Download ---------------------------------------------------------

    /// <summary>
    /// Download the on-vehicle plan for this client's <see cref="MissionType"/>.
    /// Resolves when the final <c>MISSION_ITEM_INT</c> arrives and we've sent the
    /// closing <c>MISSION_ACK(Accepted)</c>, after retry/timeout exhaustion, on
    /// cancellation, or on disposal.
    /// </summary>
    /// <param name="ct">Cancellation. On firing, the transaction completes with
    /// <see cref="MissionTransactionStatus.Cancelled"/>; whatever partial items were
    /// collected are discarded (<see cref="MissionDownloadResult.Items"/> is empty).</param>
    /// <exception cref="InvalidOperationException">Another transaction is already in flight.</exception>
    /// <exception cref="ObjectDisposedException">This client has been disposed.</exception>
    public Task<MissionDownloadResult> DownloadAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        DownloadTransaction tx;
        lock (_lock)
        {
            ThrowIfBusyLocked();
            tx = new DownloadTransaction
            {
                Tcs = new TaskCompletionSource<MissionDownloadResult>(TaskCreationOptions.RunContinuationsAsynchronously),
                StartedAt = DateTime.UtcNow,
            };
            _tx = tx;

            SendRequestList();
            // Initial wait is on MISSION_COUNT, which is a non-item message — use the default
            // per-message timeout (long) rather than the inner item timeout.
            ArmWatchdogLocked(tx, _defaultTimeout, _maxRetries, SendRequestList);
        }

        tx.CtReg = ct.Register(() => CompleteTransaction(MissionTransactionStatus.Cancelled, ackResult: null, opaqueId: null));
        return tx.Tcs.Task;
    }

    private void OnCount(MavId sender, MissionCount count, DateTime _)
    {
        if (!IsMine(sender) || count.MissionType != _missionType) return;
        lock (_lock)
        {
            if (_tx is not DownloadTransaction tx) return;
            // Vehicle has told us the plan length. Allocate the buffer and request seq 0.
            tx.ExpectedCount = count.Count;
            tx.OpaqueId = count.OpaqueId;
            tx.Items = new MissionItem[count.Count];
            tx.NextSeq = 0;

            if (count.Count == 0)
            {
                // Empty mission — per spec, GCS still closes the transaction with MISSION_ACK(Accepted).
                SendFinalAck();
                CompleteTransaction(MissionTransactionStatus.Accepted, MavMissionResult.MavMissionAccepted, tx.OpaqueId);
                return;
            }

            SendRequestItem(tx.NextSeq);
            ArmWatchdogLocked(tx, _itemTimeout, _maxRetries, () => SendRequestItem(tx.NextSeq));
        }
    }

    private void OnItemInt(MavId sender, MissionItemInt item, DateTime _)
    {
        if (!IsMine(sender) || item.MissionType != _missionType) return;
        lock (_lock)
        {
            if (_tx is not DownloadTransaction tx) return;
            if (tx.Items is null) return; // count not yet arrived — ignore stray item

            if (item.Seq != tx.NextSeq)
            {
                // Out-of-sequence — recoverable per spec. Ignore; the watchdog will
                // re-request the expected seq.
                _log?.LogDebug("Mission {Type}: out-of-sequence item seq={Got} (expected {Expected}), ignoring",
                    _missionType, item.Seq, tx.NextSeq);
                return;
            }

            tx.Items[item.Seq] = MissionItem.FromWire(item);
            var nextSeq = (ushort)(item.Seq + 1);
            if (nextSeq >= tx.ExpectedCount)
            {
                // Last item received — close the transaction with our ACK.
                SendFinalAck();
                CompleteTransaction(MissionTransactionStatus.Accepted, MavMissionResult.MavMissionAccepted, tx.OpaqueId);
                return;
            }

            tx.NextSeq = nextSeq;
            SendRequestItem(tx.NextSeq);
            ArmWatchdogLocked(tx, _itemTimeout, _maxRetries, () => SendRequestItem(tx.NextSeq));
        }
    }

    // ----- ACK (terminates upload or clear; not expected during download since we send it) ----

    private void OnAck(MavId sender, MissionAck ack, DateTime _)
    {
        if (!IsMine(sender) || ack.MissionType != _missionType) return;
        lock (_lock)
        {
            // Both upload and clear are terminated by an inbound MISSION_ACK.
            // Download ends when we send the ACK, so an inbound ACK during download is ignored.
            if (_tx is not (UploadTransaction or ClearTransaction)) return;
        }
        if (ack.Type == MavMissionResult.MavMissionAccepted)
            CompleteTransaction(MissionTransactionStatus.Accepted, ack.Type, ack.OpaqueId);
        else
            CompleteTransaction(MissionTransactionStatus.Rejected, ack.Type, opaqueId: null);
    }

    // ----- Watchdog ---------------------------------------------------------

    /// <summary>Arm a single fire-once watchdog. If it fires before being superseded,
    /// it either invokes <paramref name="resend"/> (retries remaining) or completes
    /// the transaction as <see cref="MissionTransactionStatus.Timeout"/>.</summary>
    private void ArmWatchdogLocked(Transaction tx, TimeSpan wait, int retriesLeft, Action resend)
    {
        tx.Watchdog?.Cancel();
        tx.Watchdog?.Dispose();
        var cts = new CancellationTokenSource();
        tx.Watchdog = cts;

        _ = Task.Delay(wait, cts.Token).ContinueWith(
            _ => OnWatchdogTick(tx, cts, wait, retriesLeft, resend),
            CancellationToken.None,
            TaskContinuationOptions.NotOnCanceled,
            TaskScheduler.Default);
    }

    private void OnWatchdogTick(Transaction tx, CancellationTokenSource cts, TimeSpan wait, int retriesLeft, Action resend)
    {
        bool exhausted = false;
        lock (_lock)
        {
            // Stale fire: this CTS was already replaced by a fresher arming. Bail.
            if (!ReferenceEquals(tx.Watchdog, cts)) return;
            // Transaction already completed by another path. Bail.
            if (!ReferenceEquals(_tx, tx)) return;

            if (retriesLeft <= 0)
            {
                exhausted = true;
            }
            else
            {
                resend();
                ArmWatchdogLocked(tx, wait, retriesLeft - 1, resend);
                return;
            }
        }
        if (exhausted)
            CompleteTransaction(MissionTransactionStatus.Timeout, ackResult: null, opaqueId: null);
    }

    // ----- Send helpers -----------------------------------------------------

    private void SendCount(ushort count) =>
        _connection.Send(new MissionCount(
            Count: count,
            TargetSystem: _targetSystem,
            TargetComponent: _targetComponent,
            MissionType: _missionType,
            OpaqueId: 0));

    private void SendItem(MissionItem item, ushort seq) =>
        _connection.Send(item.ToWire(seq, _targetSystem, _targetComponent, _missionType));

    private void SendRequestList() =>
        _connection.Send(new MissionRequestList(
            TargetSystem: _targetSystem,
            TargetComponent: _targetComponent,
            MissionType: _missionType));

    private void SendRequestItem(ushort seq) =>
        _connection.Send(new MissionRequestInt(
            Seq: seq,
            TargetSystem: _targetSystem,
            TargetComponent: _targetComponent,
            MissionType: _missionType));

    private void SendClearAll() =>
        _connection.Send(new MissionClearAll(
            TargetSystem: _targetSystem,
            TargetComponent: _targetComponent,
            MissionType: _missionType));

    private void SendFinalAck() =>
        _connection.Send(new MissionAck(
            TargetSystem: _targetSystem,
            TargetComponent: _targetComponent,
            Type: MavMissionResult.MavMissionAccepted,
            MissionType: _missionType,
            OpaqueId: 0));

    // ----- Common ------------------------------------------------------------

    private bool IsMine(MavId sender) =>
        sender.SystemId == _targetSystem && sender.ComponentId == _targetComponent;

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private void ThrowIfBusyLocked()
    {
        if (_tx is not null)
            throw new InvalidOperationException(
                $"A mission transaction is already in progress for {_missionType} on sys{_targetSystem}/comp{_targetComponent}.");
    }

    private void CompleteTransaction(MissionTransactionStatus status, MavMissionResult? ackResult, uint? opaqueId)
    {
        Transaction? tx;
        lock (_lock)
        {
            tx = _tx;
            if (tx is null) return;
            _tx = null;
            tx.Watchdog?.Cancel();
            tx.Watchdog?.Dispose();
            tx.Watchdog = null;
        }
        tx.CtReg.Dispose();
        var elapsed = DateTime.UtcNow - tx.StartedAt;
        tx.Complete(status, ackResult, opaqueId, elapsed);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _connection.MissionRequestIntReceived -= OnRequestInt;
        _connection.MissionRequestReceived    -= OnRequest;
        _connection.MissionAckReceived        -= OnAck;
        _connection.MissionCountReceived      -= OnCount;
        _connection.MissionItemIntReceived    -= OnItemInt;
        // Any in-flight transaction resolves as Cancelled so the awaiter doesn't hang.
        CompleteTransaction(MissionTransactionStatus.Cancelled, ackResult: null, opaqueId: null);
    }
}
