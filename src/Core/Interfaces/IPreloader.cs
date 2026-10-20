using Magic.Contexts.Assets;
using Magic.Contexts.Subscriptions;

namespace Magic.Interfaces;

/// <summary>
/// A gem that needs time to take in what spawns before it can draw or simulate it: a renderer uploads a model and
/// compiles its materials' pipelines, a physics gem shapes a body like its model. The world hands it the prefabs a
/// domain is about to spawn, before spawning any of them, and spawns once every such gem is done, so a domain is there
/// whole in its first frame: nothing dissolves in, stands still or starts late. A gem is one by implementing it; like
/// <see cref="ILogSink"/>, it is not provided to other gems, so any number of gems can be one.
/// </summary>
public interface IPreloader
{
    /// <summary>
    /// Takes in everything <paramref name="prefabs"/> name that this gem needs before it can draw or simulate them.
    /// The task ends once all of it is in, or cannot be (what does not load is drawn as the failure), telling
    /// <paramref name="progress"/> how far it is, 0 to 1. What it hands back keeps that taken in until it is disposed,
    /// from any thread, which the domain does once it closes, so nothing is let go between coming in and being drawn.
    /// Any thread.
    /// </summary>
    Task<Subscription> PreloadAsync(IReadOnlyList<Prefab> prefabs, IProgress<float> progress, CancellationToken cancel);
}
