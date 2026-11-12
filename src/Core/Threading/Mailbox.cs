using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Magic.Threading;

/// <summary>
/// Gives one reader thread the newest value that one writer thread fills, and neither thread waits.
/// </summary>
/// <remarks>
/// The writer fills <see cref="Back"/> and publishes it. The reader takes the newest published value and returns it
/// when done with it. A value published and not yet taken becomes the next back, so the newest wins. Values are reused,
/// as in a pool, so fill each value whole. Use one writer and one reader.
/// </remarks>
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
    /// Gives <see cref="Back"/> to the reader. The writer's thread.
    /// </summary>
    /// <remarks>
    /// It replaces a value that the reader has not taken, and that value becomes the next back. Else the next back is a
    /// returned value or a new one.
    /// </remarks>
    public void Publish() =>
        Back = Interlocked.Exchange(ref _published, Back) ?? (_returned.TryDequeue(out T? value) ? value : new T());

    /// <summary>
    /// Whether a value was published since the last take, with the newest in <paramref name="value"/>, the reader's
    /// until it is returned. The reader's thread.
    /// </summary>
    public bool TryTake([NotNullWhen(true)] out T? value)
    {
        value = Interlocked.Exchange(ref _published, null);

        return value is not null;
    }

    /// <summary>
    /// The newest published value, or the one taken before when none is newer; null until one is published. The
    /// reader's thread.
    /// </summary>
    /// <remarks>
    /// The value that it replaces goes back to the writer to fill again. Use it in place of <see cref="TryTake"/> and
    /// <see cref="Return"/> when the reader keeps using the newest value.
    /// </remarks>
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
