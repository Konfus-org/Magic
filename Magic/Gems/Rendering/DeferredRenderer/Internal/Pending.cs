using System.Diagnostics.CodeAnalysis;

namespace DeferredRendererGem;

/// <summary>
/// Work running off the render thread, by key: shader compiles, asset loads. Starting a job for a key that already has
/// one running replaces it: only the newest job is ever delivered, so a change made while an older one runs is never
/// lost, and the older one is cancelled. The render system polls it once a frame.
/// </summary>
internal sealed class Pending<TKey, TValue> where TKey : notnull
{
    private readonly Dictionary<TKey, (Task<TValue> Job, CancellationTokenSource Cancel)> _running = [];
    private readonly List<(TKey Key, Task<TValue> Job)> _finished = [];

    public int InFlight => _running.Count;

    /// <summary>
    /// Starts the job with the token that is cancelled when another replaces it. The job is this table's from here:
    /// <see cref="Poll"/> delivers it, done or thrown, unless <see cref="TryTake"/> or <see cref="Remove"/> takes it first.
    /// </summary>
    public void Start(TKey key, Func<CancellationToken, Task<TValue>> start)
    {
        Remove(key);

        CancellationTokenSource cancel = new();
        _running[key] = (start(cancel.Token), cancel);
    }

    /// <summary>
    /// The value of <paramref name="key"/>'s job here and now, when it has already run to the end (a pooled asset);
    /// the job is then delivered no more. False while it runs, or when it threw: <see cref="Poll"/> delivers that.
    /// </summary>
    public bool TryTake(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        if (!_running.TryGetValue(key, out (Task<TValue> Job, CancellationTokenSource Cancel) running) || !running.Job.IsCompletedSuccessfully)
        {
            value = default;
            return false;
        }

        value = running.Job.Result;
        Remove(key);
        return true;
    }

    public bool IsRunning(TKey key)
    {
        return _running.ContainsKey(key);
    }

    /// <summary>
    /// Cancels <paramref name="key"/>'s job, which is then never delivered.
    /// </summary>
    public void Remove(TKey key)
    {
        if (!_running.Remove(key, out (Task<TValue> Job, CancellationTokenSource Cancel) replaced))
            return;

        replaced.Cancel.Cancel();
        replaced.Cancel.Dispose();
    }

    /// <summary>
    /// Blocks until <paramref name="key"/>'s job is done (start-up only).
    /// </summary>
    public void Wait(TKey key)
    {
        if (_running.TryGetValue(key, out (Task<TValue> Job, CancellationTokenSource Cancel) running))
            ((IAsyncResult)running.Job).AsyncWaitHandle.WaitOne(); // a faulted job is delivered, not thrown here
    }

    /// <summary>
    /// Hands every finished job to <paramref name="done"/> with <paramref name="state"/>: one that ran to the end, or
    /// one that threw, which <paramref name="done"/> tells apart. Looks at running jobs only.
    /// </summary>
    public void Poll<TState>(TState state, Action<TState, TKey, Task<TValue>> done)
    {
        if (_running.Count == 0)
            return;

        foreach ((TKey key, (Task<TValue> job, _)) in _running)
        {
            if (job.IsCompleted)
                _finished.Add((key, job));
        }

        foreach ((TKey key, Task<TValue> job) in _finished)
        {
            if (_running.Remove(key, out (Task<TValue> Job, CancellationTokenSource Cancel) delivered))
                delivered.Cancel.Dispose();

            done(state, key, job);
        }

        _finished.Clear();
    }
}
