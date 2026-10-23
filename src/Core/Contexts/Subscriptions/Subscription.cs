namespace Magic.Contexts.Subscriptions;

/// <summary>
/// Something handed out that lasts until it is disposed: a registered command or system. Whoever keeps one
/// holds it as a Subscription, so the field says what it is and why it is kept.
/// </summary>
public sealed class Subscription(Action end) : IDisposable
{
    private Action? _end = end;

    /// <summary>
    /// One with nothing to end, for whatever must hand one out and has nothing of its own to stop.
    /// </summary>
    public static Subscription None { get; } = new(static () => { });

    /// <summary>
    /// Ends it; only the first call does anything.
    /// </summary>
    public void Dispose() => Interlocked.Exchange(ref _end, null)?.Invoke();
}
