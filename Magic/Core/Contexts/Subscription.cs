namespace Magic.Utils;

/// <summary>
/// Something that ends when disposed: a watch, a registration, an attachment. Made by whoever hands one out, with
/// what ending it does. Dispose it where its maker says; twice ends it once.
/// </summary>
public sealed class Subscription(Action end) : IDisposable
{
    private int _ended;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _ended, 1) == 0)
            end();
    }
}
