using Magic.Assets;
using Magic.Assets.Types;
using Magic.Worlds;

namespace Magic.Ecs.Components;

/// <summary>
/// Which content put an entity in the world: what the world finds a content's entities by.
/// </summary>
public record struct Origin : IComponent
{
    /// <summary>
    /// The domain that placed it; none for a spawned asset.
    /// </summary>
    public Handle<Domain> Domain;

    /// <summary>
    /// The content it is part of (<see cref="WorldContent.Id"/>).
    /// </summary>
    public ulong Content;

    /// <summary>
    /// The prefab it came from; none for the player, as what the player has done outlives a change to its prefab.
    /// </summary>
    public Handle<Prefab> Prefab;

    /// <summary>
    /// Where it was put.
    /// </summary>
    public Transform At;
}
