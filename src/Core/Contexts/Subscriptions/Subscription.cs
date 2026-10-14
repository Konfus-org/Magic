namespace Magic.Contexts.Subscriptions;

/// <summary>
/// Something handed out that lasts until it is disposed: a registered command, an added system. Whoever keeps one
/// holds it as a Subscription, so the field says what it is and why it is kept.
/// </summary>
public sealed class Subscription(Action end) : IDisposable
{
    private Action? _end = end;

    /// <summary>
    /// Ends it; only the first call does anything.
    /// </summary>
    public void Dispose()
    {
        Interlocked.Exchange(ref _end, null)?.Invoke();
    }
}
