using System.Collections.Immutable;
using Magic.Ecs;

namespace Magic.Assets.Types;

/// <summary>
/// A bundle of entities, spawned together wherever it is put: a tree of parts, each one entity. A prefab it places is a
/// part whose children are that prefab's parts. Positions are relative to where it is put. A gem reads its files.
/// </summary>
public sealed class Prefab : IAsset<Prefab>
{
    public required ImmutableArray<PrefabPart> Parts { get; init; }
}

/// <summary>
/// One part of a <see cref="Prefab"/>, spawned as one entity: its components, its tags among them, and the parts under
/// it, whose transforms are relative to its own.
/// </summary>
public sealed class PrefabPart
{
    /// <summary>
    /// What messages call it.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    public ImmutableArray<IComponent> Components { get; init; } = [];

    public ImmutableArray<PrefabPart> Children { get; init; } = [];
}
