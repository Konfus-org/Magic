using System.Collections.Immutable;
using Magic.Ecs;

namespace Magic.Worlds;

/// <summary>
/// A gem that takes part as a domain, or a saved world, replaces what is in the world: it cleans up after what leaves, and
/// prepares what comes, while the loading screen shows. The world awaits every gem's hook before it goes on, so nothing
/// left over bleeds in and nothing half ready shows. Each hook starts on the game thread; implement the ones the gem needs.
/// </summary>
public interface IHookIntoDomainLoading
{
    /// <summary>
    /// What was in the world left, as before the loading screen shows and before what replaces it comes: drop what was built
    /// up from it, such as a renderer's history, so none of it shows in what comes next.
    /// </summary>
    Task OnDomainUnloadedAsync(CancellationToken cancel) => Task.CompletedTask;

    /// <summary>
    /// What is loading in place of the world, as the components of each entity it makes or changes, before any is: ready what
    /// they need to be shown complete from their first frame, such as their models and textures on the GPU.
    /// </summary>
    Task OnDomainLoadingAsync(ImmutableArray<ImmutableArray<IComponent>> entities, CancellationToken cancel) =>
        Task.CompletedTask;
}
