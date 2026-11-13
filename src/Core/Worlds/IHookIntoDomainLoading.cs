using System.Collections.Immutable;
using Magic.Ecs;

namespace Magic.Worlds;

/// <summary>
/// A gem that cleans up after what leaves the world and prepares what comes, while a domain or a save replaces it.
/// </summary>
/// <remarks>
/// The loading screen shows while the hooks run. The world waits for the hook of each gem before it continues, so
/// nothing left over shows and nothing half ready shows. Each hook starts on the game thread. Implement only the hooks
/// that the gem needs.
/// </remarks>
public interface IHookIntoDomainLoading
{
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
}
