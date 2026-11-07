using Magic.Files;
using Magic.Files.Types;

namespace Magic.Ecs.Components;

/// <summary>
/// The domain that owns an entity its files make: the domain, the prefab it placed, and how many entities that prefab
/// made in the domain before this one. It is the same in every run, so a save finds what changed in each entity a
/// domain's files make; an entity made as the game runs has none.
/// </summary>
public record struct Owner(Handle<Domain> Domain, Handle<Prefab> Prefab, int Index) : IComponent;
