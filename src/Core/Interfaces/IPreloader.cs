using Magic.Contexts.Assets;
using Magic.Contexts.Subscriptions;

namespace Magic.Interfaces;

/// <summary>
/// A gem that needs time with what a domain spawns before it can draw, simulate or play it as it should: a renderer
/// uploads a model and compiles its materials' pipelines, then draws the world until its light and shadows settle, a
/// physics gem shapes a body like its model, the gem that runs scripts starts them. The world hands it the prefabs a
/// domain is about to spawn, before spawning any of them, and spawns once every such gem has taken them in; a domain
/// that replaces the world is then readied by each, spawned and out of sight, and shown once every one has. So a domain
/// is there whole in its first frame: nothing dissolves in, stands still or starts late. A gem is one by implementing
/// it; like <see cref="ILogSink"/>, it is not provided to other gems, so any number of gems can be one.
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

    /// <summary>
    /// Readies the domain this gem took in, once it has spawned in place of the world and before it is shown: the
    /// window keeps the loading domain's last image, and nothing that runs the world runs, steps or plays but the
    /// scripts' starts until <see cref="Contexts.ECS.WorldLoading"/> says the world is shown, heard the frame after it
    /// first is. The task ends once this gem draws, simulates or plays the domain as it should from the frame it is
    /// shown; the world is shown once every gem's has. Called on the main thread; the task ends on any.
    /// </summary>
    Task ReadyAsync(CancellationToken cancel);
}
