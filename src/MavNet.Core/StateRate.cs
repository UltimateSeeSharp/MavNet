namespace MavNet.Core;

/// <summary>
/// How often a state-change subscriber wants to be called. Choose with the named
/// factories — <see cref="Raw"/>, <see cref="Hz"/>, <see cref="Every"/> — rather
/// than passing raw <see cref="TimeSpan"/> values, so call sites stay self-documenting.
///
/// <para><see cref="Raw"/> fires on every state-affecting packet (full fidelity, no
/// throttling — appropriate for telemetry recorders, packet loggers, replay capture).
/// Throttled rates fire at most once per <see cref="MinInterval"/> per subscriber.</para>
/// </summary>
public readonly struct StateRate : IEquatable<StateRate>
{
    /// <summary>Minimum interval between successive deliveries. <see cref="TimeSpan.Zero"/> = no throttling.</summary>
    public TimeSpan MinInterval { get; }

    /// <summary>Fire on every state-affecting packet — no throttling.</summary>
    public static StateRate Raw { get; } = new(TimeSpan.Zero);

    /// <summary>At most <paramref name="hz"/> deliveries per second.</summary>
    public static StateRate Hz(double hz)
    {
        if (hz <= 0) throw new ArgumentOutOfRangeException(nameof(hz), "Rate must be positive.");
        return new(TimeSpan.FromSeconds(1.0 / hz));
    }

    /// <summary>At most one delivery per <paramref name="interval"/>.</summary>
    public static StateRate Every(TimeSpan interval)
    {
        if (interval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval), "Interval must be non-negative.");
        return new(interval);
    }

    private StateRate(TimeSpan minInterval) => MinInterval = minInterval;

    public bool IsRaw => MinInterval == TimeSpan.Zero;

    public bool Equals(StateRate other) => MinInterval == other.MinInterval;
    public override bool Equals(object? obj) => obj is StateRate r && Equals(r);
    public override int GetHashCode() => MinInterval.GetHashCode();
    public override string ToString() => IsRaw ? "raw" : $"≥ {MinInterval.TotalMilliseconds:F0} ms";

    public static bool operator ==(StateRate a, StateRate b) => a.Equals(b);
    public static bool operator !=(StateRate a, StateRate b) => !a.Equals(b);
}
