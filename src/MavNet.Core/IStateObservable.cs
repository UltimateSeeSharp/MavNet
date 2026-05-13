namespace MavNet.Core;

/// <summary>
/// A source of state-change notifications with caller-controlled delivery rate.
/// <see cref="StateRate.Raw"/> delivers on every state-affecting packet; throttled
/// rates deliver at most once per <see cref="StateRate.MinInterval"/> per subscriber.
///
/// <para>Multiple subscribers at different rates are supported and isolated — one
/// slow subscriber does not throttle the others.</para>
/// </summary>
public interface IStateObservable
{
    /// <summary>
    /// Subscribe to state-change notifications at the given rate. The handler may run on
    /// any thread; consumers needing UI-thread marshalling must do it themselves.
    /// </summary>
    /// <returns>A handle whose <see cref="IDisposable.Dispose"/> unsubscribes.</returns>
    StateSubscription SubscribeState(Action handler, StateRate rate);
}

/// <summary>
/// A state-change source that can also deliver an immutable snapshot per fire.
/// Prefer this overload for recording, replay, alerting, or any consumer that needs
/// thread-safe reads of multiple fields (no torn reads across separate property gets).
/// </summary>
/// <typeparam name="TState">An immutable snapshot type (typically a <c>readonly record struct</c>).</typeparam>
public interface IStateObservable<TState> : IStateObservable where TState : struct
{
    StateSubscription SubscribeState(Action<TState> handler, StateRate rate);
}
