using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Magic.Threading;

/// <summary>
/// The newest of what one thread fills, for another to take without either waiting: the writer fills <see cref="Back"/>
/// and publishes it, and the reader takes the newest published and returns it once done with it. A value published and
/// not yet taken becomes the next back, so the newest wins. Values are reused, as a pool's are, so each is filled whole.
/// One writer and one reader.
/// </summary>
public sealed class Mailbox<T>
    where T : class, new()
{
    private readonly ConcurrentQueue<T> _returned = new();
    private T? _published;

    /// <summary>
    /// The value <see cref="TakeNewest"/> took last, the reader's until a newer one is.
    /// </summary>
    private T? _taken;

    /// <summary>
    /// The value the writer fills next, holding whatever it held last time. The writer's thread.
    /// </summary>
    public T Back { get; private set; } = new();

    /// <summary>
    /// Hands <see cref="Back"/> to the reader, in place of one it hasn't taken, which becomes the next back; or one it
    /// returned, or a new one. The writer's thread.
    /// </summary>
    public void Publish() =>
        Back = Interlocked.Exchange(ref _published, Back) ?? (_returned.TryDequeue(out T? value) ? value : new T());

    /// <summary>
    /// Whether a value was published since the last take, with the newest in <paramref name="value"/>, the reader's until
    /// it is returned. The reader's thread.
    /// </summary>
    public bool TryTake([NotNullWhen(true)] out T? value)
    {
        value = Interlocked.Exchange(ref _published, null);

        return value is not null;
    }

    /// <summary>
    /// The newest value published, or the one taken before while none is newer; null until one is published. The value
    /// it replaces is returned for the writer to fill again. For a reader that keeps using the newest, in place of
    /// <see cref="TryTake"/> and <see cref="Return"/>. The reader's thread.
    /// </summary>
    public T? TakeNewest()
    {
        if (TryTake(out T? newest))
        {
            if (_taken is not null)
                Return(_taken);

            _taken = newest;
        }

        return _taken;
    }

    /// <summary>
    /// Gives a taken value back for the writer to fill again. The reader's thread.
    /// </summary>
    public void Return(T value) => _returned.Enqueue(value);
}
