namespace MavNet.Core;

/// <summary>
/// Handle to a single state-change subscription. Dispose to unsubscribe — there is no
/// matching <c>-=</c> form. Implements <see cref="IDisposable"/> so it composes cleanly
/// with <c>using</c>/<c>using var</c> and the Blazor <see cref="IDisposable"/> page pattern.
///
/// <para>Disposal is idempotent and thread-safe.</para>
/// </summary>
public sealed class StateSubscription : IDisposable
{
    /// <summary>Rate this subscription was created with.</summary>
    public StateRate Rate { get; }

    private int _disposed;
    private readonly Action _onDispose;

    /// <summary>Creates a subscription with the given rate and disposal callback.</summary>
    /// <param name="rate">Rate this subscription was registered at.</param>
    /// <param name="onDispose">Called once on first <see cref="Dispose"/>.</param>
    public StateSubscription(StateRate rate, Action onDispose)
    {
        ArgumentNullException.ThrowIfNull(onDispose);
        Rate = rate;
        _onDispose = onDispose;
    }

    /// <summary><c>true</c> until <see cref="Dispose"/> is called.</summary>
    public bool IsActive => Volatile.Read(ref _disposed) == 0;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _onDispose(); } catch { /* never throw from Dispose */ }
    }
}
