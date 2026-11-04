using System.Diagnostics.CodeAnalysis;
using Magic.Debugging;
using Magic.Gems;
using Magic.Threading;

namespace Magic.Pipes;

/// <summary>
/// The pipeline over <typeparamref name="TState"/> as a whole: what registered for it, in order, Core's first, then each
/// gem's as it is made, and the walk over them. What one of Core's throws is a bug in Core, and isn't caught.
/// </summary>
internal sealed class PipelineRunner<TState>(Pipelines pipelines, Threads threads) : IPipelineRunner
{
    /// <summary>
    /// Replaced whole on the main thread, and read whole on any, so a run walks them as they were when it began.
    /// </summary>
    private Nested[] _nested = [];

    /// <summary>
    /// Runs every one in order now, each returning finished, and returns the state as it ended. A gem's that throws, or
    /// returns unfinished, is logged and the gem leaves every pipeline, but for a
    /// <see cref="PipelineRefusedException"/>, which comes here to the caller. Main thread.
    /// </summary>
    public TState Run(TState state, CancellationToken cancel)
    {
        if (!threads.IsCurrent(EngineThread.Main))
        {
            throw new InvalidOperationException(
                $"The pipeline over {typeof(TState).Name} runs now only on the main thread: post it from this one."
            );
        }

        foreach (Nested nested in Volatile.Read(ref _nested))
        {
            state = RunWithinCall(nested, state, cancel);
        }

        return state;
    }

    /// <summary>
    /// Runs every one in order, each once the one before it has ended, on whichever thread that ended on. A gem's that
    /// throws is logged, and the run goes on with the state as it was. Any thread.
    /// </summary>
    public ValueTask<TState> RunAsync(TState state, CancellationToken cancel) =>
        WalkAsync(state, isReversed: false, cancel);

    /// <summary>
    /// <see cref="RunAsync"/>, last first: for what takes apart what the forward order put together.
    /// </summary>
    public ValueTask<TState> RunReversedAsync(TState state, CancellationToken cancel) =>
        WalkAsync(state, isReversed: true, cancel);

    /// <summary>
    /// Nests <paramref name="pipeline"/>, last. Core's register before any gem is made, so they run first. Main thread.
    /// </summary>
    internal void Add(IPipeline<TState> pipeline) =>
        Volatile.Write(ref _nested, [.. _nested, new(pipeline, pipeline.GetType().Name, pipeline is IGem)]);

    void IPipelineRunner.Remove(object pipeline) =>
        Volatile.Write(ref _nested, Array.FindAll(_nested, nested => !ReferenceEquals(nested.Pipeline, pipeline)));

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
        catch (Exception ex) when (nested.IsGem && ex is not PipelineRefusedException)
        {
            Log.Error($"Gem {nested.Name} failed in {typeof(TState).Name} and stops running: {ex}");
            pipelines.Remove(nested.Pipeline);

            return state;
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever a gem throws, it is logged and the run goes on without it."
    )]
    private async ValueTask<TState> WalkAsync(TState state, bool isReversed, CancellationToken cancel)
    {
        Nested[] all = Volatile.Read(ref _nested);
        for (int i = 0; i < all.Length; i++)
        {
            Nested nested = all[isReversed ? all.Length - 1 - i : i];
            try
            {
                state = await nested.Pipeline.RunAsync(state, cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (nested.IsGem && ex is not PipelineRefusedException)
            {
                Log.Error($"Gem {nested.Name} failed in {typeof(TState).Name}: {ex}");
            }
        }

        return state;
    }

    /// <summary>
    /// One nested pipeline: what messages call it, and whether a gem registered it, which is what a throw is caught for.
    /// </summary>
    private readonly record struct Nested(IPipeline<TState> Pipeline, string Name, bool IsGem);
}
