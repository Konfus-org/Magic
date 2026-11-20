namespace Magic.Utils;

/// <summary>
/// Base for a class that owns resources. It disposes what the class tracks, last tracked first, one time.
/// </summary>
/// <remarks>
/// Call <see cref="Track{T}(T)"/> or <see cref="Track(Action)"/> where the class makes or gets what it owns. Override
/// <see cref="Dispose(bool)"/> only for cleanup that tracking cannot express.
/// </remarks>
public abstract class Disposable : IDisposable
{
    private int _disposed; // 0 = active, 1 = disposed

    private List<IDisposable>? _disposables;

    /// <summary>
    /// Gets a value indicating whether this instance has been disposed.
    /// </summary>
    protected bool IsDisposed => Volatile.Read(ref _disposed) == 1;

    /// <summary>
    /// Disposes this instance and everything it tracks.
    /// </summary>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Disposes the tracked resources, last tracked first, the first time it runs.
    /// </summary>
    /// <param name="disposing">True when <see cref="Dispose()"/> called it.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (disposing && _disposables is not null)
        {
            for (int i = _disposables.Count - 1; i >= 0; i--)
            {
                _disposables[i].Dispose();
            }

            _disposables.Clear();
            _disposables = null;
        }
    }

    /// <summary>
    /// Registers an <see cref="IDisposable"/> resource to be automatically disposed when this instance is disposed.
    /// </summary>
    protected T Track<T>(T disposable)
        where T : IDisposable
    {
        ArgumentNullException.ThrowIfNull(disposable);

        if (IsDisposed)
        {
            disposable.Dispose();
            throw new ObjectDisposedException(GetType().Name);
        }

        _disposables ??= [];
        _disposables.Add(disposable);
        return disposable;
    }

    /// <summary>
    /// Registers a cleanup callback to run when this instance is disposed.
    /// </summary>
    protected void Track(Action disposeAction)
    {
        ArgumentNullException.ThrowIfNull(disposeAction);
        Track(new CallbackDisposable(disposeAction));
    }

    /// <summary>
    /// Throws an <see cref="ObjectDisposedException"/> if this instance has been disposed.
    /// </summary>
    protected void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);

    private sealed class CallbackDisposable(Action action) : IDisposable
    {
        private Action? _action = action;

        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }
}
