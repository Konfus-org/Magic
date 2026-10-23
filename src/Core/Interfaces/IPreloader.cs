using Magic.Contexts.Assets;
using Magic.Contexts.Subscriptions;

namespace Magic.Interfaces;

/// <summary>
/// A gem that needs time with what a domain spawns before it can draw, simulate or play it as it should: a renderer
/// uploads a model and compiles its materials' pipelines, then draws the world until its light and shadows settle, a
/// physics gem shapes a body like its model, the gem that runs scripts starts them. The world hands it the prefabs a
/// domain is about to spawn, before spawning any of them, and spawns once every such gem has taken them in; a domain
/// that replaces the world is then readied by each, spawned and out of sight, and shown once every one has, as is a
/// saved world put back. So a domain is there whole in its first frame: nothing dissolves in, stands still or starts
/// late. Each time a new world takes the old one's place, every such gem first forgets the old one, so nothing of it
/// carries over. A gem is one by implementing it; like <see cref="ILogSink"/>, it is not provided to other gems, so
/// any number of gems can be one.
/// </summary>
public interface IPreloader
{
    /// <summary>
    /// Forgets what this gem built up from the world, as a new one takes its place: whatever it worked out over frames
    /// from what was there, which the new world would otherwise start from, and whatever it holds for the old world
    /// alone. Called on the main thread once the old world's entities are destroyed, all but its globals, and before
    /// anything of the new one spawns: as a domain begins to replace the world, before the loading domain shows; as it
    /// arrives, once the loading domain has closed; and as a saved world is put back. The task ends once it has, on any
    /// thread; the new world is taken in (<see cref="PreloadAsync"/>), and shown, only once every gem's has. A gem with
    /// nothing to forget leaves it as it is.
    /// </summary>
    Task ForgetAsync(CancellationToken cancel) => Task.CompletedTask;

    /// <summary>
    /// Takes in everything <paramref name="prefabs"/> name that this gem needs before it can draw or simulate them.
    /// The task ends once all of it is in, or cannot be (what does not load is drawn as the failure), telling
    /// <paramref name="progress"/> how far it is, 0 to 1. What it hands back keeps that taken in until it is disposed,
    /// from any thread, which the domain does once it closes, so nothing is let go between coming in and being drawn.
    /// Any thread.
    /// </summary>
    Task<Subscription> PreloadAsync(IReadOnlyList<Prefab> prefabs, IProgress<float> progress, CancellationToken cancel);

    /// <summary>
    /// Readies the world, out of sight, once a domain this gem took in has spawned in place of it and before it is
    /// shown: from this call the window keeps the last image it showed as the world loaded, the loading domain's, or
    /// black when there is none, and nothing that runs the world runs, steps or plays but the scripts' starts until
    /// <see cref="Contexts.ECS.WorldLoading"/> says the world is shown, heard the frame after it first is. A saved world
    /// put back is readied as it is put back, so it is held from its first frame, and again once its domains have been
    /// taken in. A call while one is under way takes its place, and that one's task ends cancelled. The task ends once
    /// this gem draws, simulates or plays the world as it should from the frame it is shown; the world is shown once
    /// every gem's has. Called on the main thread; the task ends on any.
    /// </summary>
    Task ReadyAsync(CancellationToken cancel);
}
