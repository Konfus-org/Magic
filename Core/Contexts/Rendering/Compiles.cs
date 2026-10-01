using Magic.Utils;

namespace Magic.Contexts.Rendering;

/// <summary>
/// Shader compiles running on workers, by key. Starting a compile for a key that already has one running replaces it:
/// only the newest job's result is ever delivered, so an edit made while an older compile runs is never lost.
/// </summary>
internal sealed class Compiles<TKey> where TKey : notnull
{
    private readonly Dictionary<TKey, Task<Result<CompiledShader>>> _running = [];
    private readonly List<(TKey Key, Task<Result<CompiledShader>> Job)> _done = [];

    public int InFlight => _running.Count;

    public void Start(TKey key, Task<Result<CompiledShader>> job)
    {
        _running[key] = job;
    }

    public bool IsRunning(TKey key)
    {
        return _running.ContainsKey(key);
    }

    /// <summary>Blocks until <paramref name="key"/>'s job is done (start-up only).</summary>
    public void Wait(TKey key)
    {
        if (_running.TryGetValue(key, out Task<Result<CompiledShader>>? job))
            job.Wait();
    }

    /// <summary>Hands every finished job's result to <paramref name="done"/> with <paramref name="state"/>, main thread. Looks at running jobs only.</summary>
    public void Poll<TState>(TState state, Action<TState, TKey, Result<CompiledShader>> done)
    {
        if (_running.Count == 0)
            return;

        foreach ((TKey key, Task<Result<CompiledShader>> job) in _running)
        {
            if (job.IsCompleted)
                _done.Add((key, job));
        }

        foreach ((TKey key, Task<Result<CompiledShader>> job) in _done)
        {
            _running.Remove(key);
            done(state, key, job.IsCompletedSuccessfully
                ? job.Result
                : Result<CompiledShader>.Failure(job.Exception?.GetBaseException().Message ?? "compile faulted"));
        }

        _done.Clear();
    }
}
