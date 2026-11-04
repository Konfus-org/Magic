using System.Collections.Concurrent;
using System.Reflection;
using Magic.Threading;
using Magic.Utils;

namespace Magic.Pipelines;

/// <summary>
/// Every pipeline, by the type of its state, and the way to run one: <c>pipelines.Run(state)</c> runs the pipeline over
/// that state's type now, on the main thread, and <c>RunAsync</c> awaited, on any. One nothing registered for runs nothing.
/// Register what joins a pipeline (<see cref="Register"/>) and dispose the subscription to take it out again. Any thread.
/// </summary>
public sealed class PipelineCoordinator
{
    private readonly ConcurrentDictionary<Type, IPipelineRunner> _runners = new();
    private readonly Threads _threads;

    internal PipelineCoordinator(Threads threads) => _threads = threads;

    /// <summary>
    /// Adds <paramref name="pipeline"/> to the pipeline over each state it implements an <see cref="IPipeline{TState}"/>
    /// of, last, until the subscription is disposed. Main thread.
    /// </summary>
    public Subscription Register(object pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        MethodInfo add =
            typeof(PipelineCoordinator).GetMethod(nameof(Add), BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"{nameof(PipelineCoordinator)} has no {nameof(Add)}.");
        foreach (Type contract in pipeline.GetType().GetInterfaces())
        {
            if (contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IPipeline<>))
                add.MakeGenericMethod(contract.GetGenericArguments()[0]).Invoke(this, [pipeline]);
        }

        return new Subscription(() => Remove(pipeline));
    }

    /// <summary>
    /// Runs the pipeline over <paramref name="state"/> now, each nested pipeline returning finished, and returns the state
    /// as it ended. Main thread.
    /// </summary>
    public TState Run<TState>(TState state, CancellationToken cancel = default) =>
        GetRunner<TState>().Run(state, cancel);

    /// <summary>
    /// <see cref="Run{TState}(TState, CancellationToken)"/> over a state that carries nothing in.
    /// </summary>
    public TState Run<TState>(CancellationToken cancel = default)
        where TState : struct => GetRunner<TState>().Run(default, cancel);

    /// <summary>
    /// Runs the pipeline over <paramref name="state"/> awaited, each nested pipeline once the one before it has ended, on
    /// whichever thread that ended on. Any thread.
    /// </summary>
    public ValueTask<TState> RunAsync<TState>(TState state, CancellationToken cancel) =>
        GetRunner<TState>().RunAsync(state, cancel);

    /// <summary>
    /// <see cref="RunAsync{TState}"/>, last first: for what takes apart what the forward order put together.
    /// </summary>
    public ValueTask<TState> RunReversedAsync<TState>(TState state, CancellationToken cancel) =>
        GetRunner<TState>().RunReversedAsync(state, cancel);

    /// <summary>
    /// Takes <paramref name="pipeline"/> out of every pipeline. Main thread.
    /// </summary>
    internal void Remove(object pipeline)
    {
        foreach (IPipelineRunner runner in _runners.Values)
        {
            runner.Remove(pipeline);
        }
    }

    private PipelineRunner<TState> GetRunner<TState>() =>
        (PipelineRunner<TState>)
            _runners.GetOrAdd(
                typeof(TState),
                static (_, pipelines) => new PipelineRunner<TState>(pipelines, pipelines._threads),
                this
            );

    private void Add<TState>(object pipeline) => GetRunner<TState>().Add((IPipeline<TState>)pipeline);
}
