using System.Collections.Concurrent;
using System.Reflection;
using Magic.GemAPI;
using Magic.Threading;
using Magic.Utils;

namespace Magic.Pipelines;

/// <summary>
/// Every pipeline, by the type of its state. Ask for one and say how to run it: <c>pipelines.Get&lt;MyState&gt;().Run(state)</c>
/// runs it now, and <c>.Defer(state)</c> at the frame's deferred point. One nothing joined runs nothing. Any thread.
/// </summary>
public sealed class PipelineCoordinator
{
    private readonly ConcurrentDictionary<Type, IPipelineRunner> _pipelines = new();

    /// <summary>
    /// One entry per state deferred, in the order they were, saying whose it is.
    /// </summary>
    private readonly ConcurrentQueue<IPipelineRunner> _deferred = new();

    private readonly Threads _threads;

    internal PipelineCoordinator(Threads threads) => _threads = threads;

    public PipelineRunner<TState> Get<TState>() =>
        (PipelineRunner<TState>)
            _pipelines.GetOrAdd(
                typeof(TState),
                static (_, pipelines) => new PipelineRunner<TState>(pipelines, pipelines._threads),
                this
            );

    /// <summary>
    /// Has <paramref name="gem"/> join the pipelines over <paramref name="states"/>, each an
    /// <see cref="IPipeline{TState}"/> it implements, until the subscription is disposed. Main thread.
    /// </summary>
    internal Subscription Register(IGem gem, string name, IReadOnlyList<Type> states)
    {
        MethodInfo join =
            typeof(PipelineCoordinator).GetMethod(nameof(AddGem), BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"{nameof(PipelineCoordinator)} has no {nameof(AddGem)}.");
        foreach (Type state in states)
        {
            join.MakeGenericMethod(state).Invoke(this, [gem, name]);
        }

        return new Subscription(() => Remove(gem));
    }

    /// <summary>
    /// Adds <paramref name="pipeline"/>, one of Core's, to every <see cref="IPipeline{TState}"/> it implements. Main thread.
    /// </summary>
    internal void Join(object pipeline)
    {
        MethodInfo add =
            typeof(PipelineCoordinator).GetMethod(nameof(Add), BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"{nameof(PipelineCoordinator)} has no {nameof(Add)}.");
        foreach (Type contract in pipeline.GetType().GetInterfaces())
        {
            if (contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IPipeline<>))
                add.MakeGenericMethod(contract.GetGenericArguments()[0]).Invoke(this, [pipeline]);
        }
    }

    /// <summary>
    /// Takes everything of <paramref name="gem"/> out of every pipeline. Main thread.
    /// </summary>
    internal void Remove(IGem gem)
    {
        foreach (IPipelineRunner pipeline in _pipelines.Values)
        {
            pipeline.Remove(gem);
        }
    }

    /// <summary>
    /// Notes that a state of <paramref name="runner"/>'s was deferred. Any thread.
    /// </summary>
    internal void AddDeferred(IPipelineRunner runner) => _deferred.Enqueue(runner);

    /// <summary>
    /// Runs what was deferred, in the order it was. What is deferred meanwhile waits for the next time. Main thread.
    /// </summary>
    internal void RunDeferred(CancellationToken cancel)
    {
        for (int left = _deferred.Count; left > 0 && _deferred.TryDequeue(out IPipelineRunner? runner); left--)
        {
            runner.RunNextDeferred(cancel);
        }
    }

    private void Add<TState>(object pipeline) =>
        Get<TState>().Add((IPipeline<TState>)pipeline, pipeline.GetType().Name, null);

    private void AddGem<TState>(IGem gem, string name) => Get<TState>().Add((IPipeline<TState>)gem, name, gem);
}
