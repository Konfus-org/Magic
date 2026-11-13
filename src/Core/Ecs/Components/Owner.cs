using Magic.Assets;

namespace Magic.Ecs.Components;

/// <summary>
/// The domain whose files made an entity: the domain, the prefab, and how many entities that prefab made before it.
/// </summary>
/// <remarks>
/// It is the same in each run, so a save finds what changed in each entity that the files of a domain make. An entity
/// made while the game runs has no owner.
/// </remarks>
public record struct Owner(Handle<Domain> Domain, Handle<Prefab> Prefab, int Index) : IComponent;
