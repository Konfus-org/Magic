using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Magic.Debugging;
using Magic.GemAPI;
using Magic.Threading;
using Magic.Utils;

namespace Magic.Pipelines;

/// <summary>
/// The pipeline over <typeparamref name="TState"/> as a whole: the ones nested in it, in order, Core's first, then each
/// gem's as it is made, and how to run them: now (<see cref="Run"/>), at the frame's deferred point (<see cref="Defer"/>),
/// or awaited (<see cref="RunAsync"/>). What joined can't tell which. What one of Core's throws is a bug in Core, and isn't
/// caught.
/// </summary>
public sealed class PipelineRunner<TState> : IPipeline<TState>, IPipelineRunner
{
    private readonly PipelineCoordinator _pipelines;
    private readonly Threads _threads;

    /// <summary>
    /// What was deferred and hasn't run.
    /// </summary>
    private readonly ConcurrentQueue<DeferredRun> _deferred = new();

    /// <summary>
    /// Change lists not in use, so a run needn't allocate one: a pool. Main thread.
    /// </summary>
    private readonly Stack<TState> _spares = new();

    /// <summary>
    /// Replaced whole on the main thread, and read whole on any, so a run walks them as they were when it began.
    /// </summary>
    private Nested[] _nested = [];

    /// <summary>
    /// The change list what is pushed goes onto until its deferred run begins. Main thread.
    /// </summary>
    private TState? _pending;

    internal PipelineRunner(PipelineCoordinator pipelines, Threads threads)
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
    /// Runs every one in order now, each returning finished, and returns the state as it ended. A gem's that throws, or
    /// returns unfinished, is logged and the gem leaves every pipeline, but for a
    /// <see cref="PipelineRefusedException"/>, which comes here to the caller. Main thread.
    /// </summary>
    public TState Run(TState state, CancellationToken cancel = default)
    {
        if (!_threads.IsCurrent(EngineThread.Main))
        {
            throw new InvalidOperationException(
                $"The pipeline over {typeof(TState).Name} runs now only on the main thread: defer it from this one."
            );
        }

        foreach (Nested nested in Volatile.Read(ref _nested))
        {
            state = RunWithinCall(nested, state, cancel);
        }

        return state;
    }

    /// <summary>
    /// Has <see cref="Run"/> run over <paramref name="state"/> at the frame's deferred point, after the fixed steps, in
    /// the order things were deferred; deferred at that point, it waits for the next frame. Any thread.
    /// </summary>
    public void Defer(TState state)
    {
        _deferred.Enqueue(new DeferredRun(state, IsPending: false));
        _pipelines.AddDeferred(this);
    }

    /// <summary>
    /// Runs what was deferred on this pipeline now, for an owner that needs it at another point in the frame. Main thread.
    /// </summary>
    public void RunDeferred(CancellationToken cancel = default)
    {
        for (int left = _deferred.Count; left > 0; left--)
        {
            RunNextDeferred(cancel);
        }
    }

    /// <summary>
    /// <see cref="RunAsync"/>, each called on the main thread, telling <paramref name="progress"/> as each begins and
    /// ends. Any thread.
    /// </summary>
    public ValueTask<TState> RunOnMainAsync(
        TState state,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    ) => WalkAsync(state, isOnMain: true, isReversed: false, progress, cancel);

    /// <summary>
    /// <see cref="RunOnMainAsync"/>, last first: for what takes apart what the forward order put together.
    /// </summary>
    public ValueTask<TState> RunOnMainInReverseAsync(
        TState state,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    ) => WalkAsync(state, isOnMain: true, isReversed: true, progress, cancel);

    /// <summary>
    /// The change list being pushed onto, <paramref name="make"/> making one when there is no spare, deferred as this first
    /// asks for it. Main thread.
    /// </summary>
    internal TState GetPending(Func<TState> make)
    {
        if (!_threads.IsCurrent(EngineThread.Main))
        {
            throw new InvalidOperationException(
                $"A {typeof(TState).Name} is pushed onto on the main thread: hand it what another thread built as data."
            );
        }

        if (_pending is { } pending)
            return pending;

        TState made = Rent(make);
        _pending = made;
        _deferred.Enqueue(new DeferredRun(made, IsPending: true));
        _pipelines.AddDeferred(this);

        return made;
    }

    /// <summary>
    /// A change list of the caller's until it calls <see cref="Return"/>, <paramref name="make"/> making one when there is
    /// no spare. Main thread.
    /// </summary>
    internal TState Rent(Func<TState> make) => _spares.TryPop(out TState? spare) ? spare : make();

    /// <summary>
    /// Empties <paramref name="state"/>, a change list, and keeps it for the next to rent. Main thread.
    /// </summary>
    internal void Return(TState state)
    {
        (state as IChangeList)?.Clear();
        _spares.Push(state);
    }

    /// <summary>
    /// Nests <paramref name="pipeline"/>, last. Core's join before any gem is made, so they run first. Main thread.
    /// </summary>
    internal void Add(IPipeline<TState> pipeline, string name, IGem? gem) =>
        Volatile.Write(ref _nested, [.. _nested, new(pipeline, name, gem)]);

    void IPipelineRunner.Remove(IGem gem) =>
        Volatile.Write(ref _nested, Array.FindAll(_nested, nested => !ReferenceEquals(nested.Gem, gem)));

    void IPipelineRunner.RunNextDeferred(CancellationToken cancel) => RunNextDeferred(cancel);

    private void RunNextDeferred(CancellationToken cancel)
    {
        if (!_deferred.TryDequeue(out DeferredRun deferred))
            return;

        // Before it runs, so what a gem pushes as it runs goes onto a list of its own, for the next frame.
        if (deferred.IsPending)
            _pending = default;

        TState ended = Run(deferred.State, cancel);
        if (deferred.IsPending)
            Return(ended);
    }

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
        catch (Exception ex) when (nested.Gem is not null && ex is not PipelineRefusedException)
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

        Nested[] all = Volatile.Read(ref _nested);
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
            catch (Exception ex) when (nested.Gem is not null && ex is not PipelineRefusedException)
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

    /// <summary>
    /// A state waiting for the deferred point, and whether it is the runner's own pending list.
    /// </summary>
    private readonly record struct DeferredRun(TState State, bool IsPending);
}

public static class PipelineRunnerExtensions
{
    extension<T>(PipelineRunner<T> runner)
        where T : class, IChangeList, new()
    {
        /// <summary>
        /// The list to push onto, which runs at the frame's deferred point and is then emptied. Ask for it each time you
        /// push: it is another list once its run begins. Main thread.
        /// </summary>
        public T Deferred => runner.GetPending(static () => new T());

        /// <summary>
        /// A list of the caller's own, to run now and give back with <c>Return</c>. Main thread.
        /// </summary>
        internal T Rent() => runner.Rent(static () => new T());
    }
}
