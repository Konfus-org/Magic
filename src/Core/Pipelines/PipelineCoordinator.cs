using System.Collections.Concurrent;
using System.Reflection;
using Magic.GemAPI;
using Magic.Threading;
using Magic.Utils;

namespace Magic.Pipelines;

/// <summary>
/// Every pipeline, by the type of its state: what whatever in Core owns a state runs it through. One nothing joined runs
/// nothing. Any thread.
/// </summary>
internal sealed class PipelineCoordinator
{
    private readonly ConcurrentDictionary<Type, IPipelineList> _pipelines = new();
    private readonly Threads _threads;

    internal PipelineCoordinator(Threads threads) => _threads = threads;

    public PipelineList<TState> Get<TState>() =>
        (PipelineList<TState>)
            _pipelines.GetOrAdd(
                typeof(TState),
                static (_, pipelines) => new PipelineList<TState>(pipelines, pipelines._threads),
                this
            );

    /// <summary>
    /// Has <paramref name="gem"/> join the pipelines over <paramref name="states"/>, each an
    /// <see cref="IPipeline{TState}"/> it implements, until the subscription is disposed. Main thread.
    /// </summary>
    internal Subscription Register(IGem gem, string name, IReadOnlyList<Type> states)
    {
        MethodInfo join =
            typeof(PipelineCoordinator).GetMethod(nameof(Join), BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"{nameof(PipelineCoordinator)} has no {nameof(Join)}.");
        foreach (Type state in states)
        {
            join.MakeGenericMethod(state).Invoke(this, [gem, name]);
        }

        return new Subscription(() => Remove(gem));
    }

    /// <summary>
    /// Takes everything of <paramref name="gem"/> out of every pipeline. Main thread.
    /// </summary>
    internal void Remove(IGem gem)
    {
        foreach (IPipelineList pipeline in _pipelines.Values)
        {
            pipeline.Remove(gem);
        }
    }

    private void Join<TState>(IGem gem, string name) => Get<TState>().Add((IPipeline<TState>)gem, name, gem);
}
