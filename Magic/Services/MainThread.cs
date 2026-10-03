using Magic.Contexts;

namespace Magic.Services;

/// <summary>
/// The simulation thread, where the ECS, scripts and the debug UI live: <see cref="ThreadId.Main"/> without having
/// to say so. For work that ran elsewhere (a script's own task, a worker) and has something to hand back: what it
/// posts runs at the start of the next frame, in the order it was posted. Any thread.
/// </summary>
public sealed class MainThread(Threads threads)
{
    /// <summary>
    /// Is the calling thread the main thread?
    /// </summary>
    public bool IsCurrent => threads.IsCurrent(ThreadId.Main);

    /// <inheritdoc cref="Threads.Post"/>
    public void Post(Action work)
    {
        threads.Post(ThreadId.Main, work);
    }

    /// <inheritdoc cref="Threads.Invoke(ThreadId, Action)"/>
    public void Invoke(Action work)
    {
        threads.Invoke(ThreadId.Main, work);
    }

    /// <inheritdoc cref="Threads.Invoke(ThreadId, Action)"/>
    public T Invoke<T>(Func<T> work)
    {
        return threads.Invoke(ThreadId.Main, work);
    }

    /// <inheritdoc cref="Threads.InvokeAsync(ThreadId, Action{CancellationToken}, CancellationToken)"/>
    public Task InvokeAsync(Action<CancellationToken> work, CancellationToken cancel = default)
    {
        return threads.InvokeAsync(ThreadId.Main, work, cancel);
    }

    /// <inheritdoc cref="Threads.InvokeAsync(ThreadId, Action{CancellationToken}, CancellationToken)"/>
    public Task<T> InvokeAsync<T>(Func<CancellationToken, T> work, CancellationToken cancel = default)
    {
        return threads.InvokeAsync(ThreadId.Main, work, cancel);
    }
}
