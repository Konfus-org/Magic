using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Magic.Debugging;
using Magic.GemAPI;
using Magic.Threading;
using Magic.Utils;

namespace Magic.Pipelines;

/// <summary>
/// The pipeline over <typeparamref name="TState"/> as a whole: the ones nested in it, in order, Core's first, then each
/// gem's as it is made, and how to run them. Whoever owns the state runs it. What one of Core's throws is a bug in Core,
/// and isn't caught.
/// </summary>
internal sealed class PipelineList<TState> : IPipeline<TState>, IPipelineList
{
    private readonly PipelineCoordinator _pipelines;
    private readonly Threads _threads;

    /// <summary>
    /// Replaced whole on the main thread, so a run walks them as they were when it began.
    /// </summary>
    private ImmutableArray<Nested> _nested = [];

    internal PipelineList(PipelineCoordinator pipelines, Threads threads)
    {
        _pipelines = pipelines;
        _threads = threads;
    }

    /// <summary>
    /// Runs every one in order, each once the one before it has ended, on whichever thread that ended on. A gem's that
    /// throws is logged, and the run goes on with the state as it was. Any thread.
    /// </summary>
    public ValueTask<TState> RunAsync(TState state, CancellationToken cancel) =>
        WalkAsync(state, isOnMain: false, isReversed: false, null, cancel);

    /// <summary>
    /// Runs every one in order now, each returning finished. A gem's that throws, or returns unfinished, is logged and
    /// the gem leaves every pipeline. Main thread.
    /// </summary>
    internal TState Run(TState state, CancellationToken cancel)
    {
        foreach (Nested nested in _nested)
        {
            state = RunWithinCall(nested, state, cancel);
        }

        return state;
    }

    /// <summary>
    /// <see cref="RunAsync"/>, each called on the main thread, telling <paramref name="progress"/> as each begins and
    /// ends. Any thread.
    /// </summary>
    internal ValueTask<TState> RunOnMainAsync(
        TState state,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    ) => WalkAsync(state, isOnMain: true, isReversed: false, progress, cancel);

    /// <summary>
    /// <see cref="RunOnMainAsync"/>, last first: for what takes apart what the forward order put together.
    /// </summary>
    internal ValueTask<TState> RunOnMainInReverseAsync(
        TState state,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    ) => WalkAsync(state, isOnMain: true, isReversed: true, progress, cancel);

    /// <summary>
    /// Nests <paramref name="pipeline"/> last: <paramref name="gem"/>'s, or Core's when it is none. Main thread.
    /// </summary>
    internal void Add(IPipeline<TState> pipeline, string name, IGem? gem) =>
        _nested = _nested.Add(new(pipeline, name, gem));

    void IPipelineList.Remove(IGem gem) => _nested = _nested.RemoveAll(nested => ReferenceEquals(nested.Gem, gem));

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever a gem throws, it is logged and the gem leaves the pipelines; the others go on."
    )]
    private TState RunWithinCall(Nested nested, TState state, CancellationToken cancel)
    {
        try
        {
            ValueTask<TState> running = nested.Pipeline.RunAsync(state, cancel);
            if (running.IsCompleted)
                return running.Result;

            running.AsTask().FireAndForget();

            throw new InvalidOperationException("It returned unfinished, and a frame doesn't wait.");
        }
        catch (Exception ex) when (nested.Gem is not null)
        {
            Log.Error($"Gem {nested.Name} failed in {typeof(TState).Name} and stops running: {ex}");
            _pipelines.Remove(nested.Gem);

            return state;
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever a gem throws, it is logged and the run goes on without it."
    )]
    private async ValueTask<TState> WalkAsync(
        TState state,
        bool isOnMain,
        bool isReversed,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        if (isOnMain)
            await _threads.Enter(EngineThread.Main);

        ImmutableArray<Nested> all = _nested;
        for (int i = 0; i < all.Length; i++)
        {
            Nested nested = all[isReversed ? all.Length - 1 - i : i];
            string work = $"{typeof(TState).Name}: {nested.Name}";
            progress?.Report(new LoadProgress(work, 0, 1));
            try
            {
                state = await nested.Pipeline.RunAsync(state, cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (nested.Gem is not null)
            {
                Log.Error($"Gem {nested.Name} failed in {typeof(TState).Name}: {ex}");
            }

            // Back to the main thread, wherever that one ended, for the next.
            if (isOnMain)
                await _threads.Enter(EngineThread.Main);

            progress?.Report(new LoadProgress(work, 1, 1));
        }

        return state;
    }

    /// <summary>
    /// One nested pipeline: what messages call it, and the gem that joined with it, none for Core's own.
    /// </summary>
    private readonly record struct Nested(IPipeline<TState> Pipeline, string Name, IGem? Gem);
}
