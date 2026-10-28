namespace Magic.State;

public readonly record struct Subscription(Action Unregister) : IDisposable
{
    public void Dispose() => Unregister();
}
