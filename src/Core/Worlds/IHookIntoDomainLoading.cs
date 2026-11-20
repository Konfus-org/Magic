using System.Collections.Immutable;
using Magic.Ecs;

namespace Magic.Worlds;

/// <summary>
/// A gem that cleans up after what leaves the world, prepares what comes and settles what came, while a domain or a
/// save replaces it.
/// </summary>
/// <remarks>
/// The loading screen or the cover shows while the hooks run. The world waits for the hook of each gem before it
/// continues, so nothing left over shows and nothing half ready shows. Each hook starts on the game thread. Implement
/// only the hooks that the gem needs.
/// </remarks>
public interface IHookIntoDomainLoading
{
    /// <summary>
    /// Starts to load what the entities that load in place of the world need, such as their models and textures, as
    /// soon as they are read.
    /// </summary>
    /// <remarks>
    /// It runs before the world fades to the loading screen, while the world that leaves still shows, so it changes
    /// nothing that shows. The loads go on while the fades and the loading screen show. The world waits for this hook
    /// only before <see cref="OnDomainLoadingAsync"/>, which still waits until what the entities need is ready.
    /// <paramref name="entities"/> is as <see cref="OnDomainLoadingAsync"/> gets it.
    /// </remarks>
    Task OnDomainReadAsync(ImmutableArray<ImmutableArray<IComponent>> entities, CancellationToken cancel) =>
        Task.CompletedTask;

    /// <summary>
    /// Drops what the gem made from the world that left, such as the history of a renderer, so none of it shows next.
    /// </summary>
    /// <remarks>
    /// It runs after the loading screen shows and before what replaces the world comes.
    /// </remarks>
    Task OnDomainUnloadedAsync(CancellationToken cancel) => Task.CompletedTask;

    /// <summary>
    /// Prepares what the entities that load in place of the world need to show complete from their first frame.
    /// </summary>
    /// <remarks>
    /// <paramref name="entities"/> holds the components of each entity that the load makes or changes, before the load
    /// makes any. Prepare such things as their models and textures on the GPU.
    /// </remarks>
    Task OnDomainLoadingAsync(ImmutableArray<ImmutableArray<IComponent>> entities, CancellationToken cancel) =>
        Task.CompletedTask;

    /// <summary>
    /// Returns when what the gem draws of the world that arrived has settled, such as its light and its history.
    /// </summary>
    /// <remarks>
    /// It runs after the world arrived, while a cover hides the window and the time of the world stands still. The
    /// frames continue while it runs. The cover fades into the world when the hook of each gem returned.
    /// </remarks>
    Task OnDomainArrivedAsync(CancellationToken cancel) => Task.CompletedTask;
}
