using System.Collections.Immutable;
using System.Linq;
using Magic.Ecs;
using Magic.Ecs.Components;

namespace Magic.Assets;

/// <summary>
/// A bundle of entities, spawned together wherever it is put: a tree of parts, each one entity.
/// </summary>
/// <remarks>
/// A prefab that it places is a part whose children are that prefab's parts. Positions are relative to where it is put.
/// A gem reads its files.
/// </remarks>
public sealed class Prefab : IAsset<Prefab>
{
    public required ImmutableArray<PrefabPart> Parts { get; init; }

    /// <summary>
    /// Each part of the prefab put at <paramref name="at"/>, with its transform in the world: a part before the parts
    /// under it.
    /// </summary>
    /// <remarks>
    /// It is the order in which the world spawns the parts, so a part's place in the list is the
    /// <see cref="Owner.Index"/> of the entity that it spawns as.
    /// </remarks>
    public ImmutableArray<PlacedPart> Place(Transform at)
    {
        ImmutableArray<PlacedPart>.Builder placed = ImmutableArray.CreateBuilder<PlacedPart>();
        AddPlaced(Parts, at, placed);

        return placed.ToImmutable();
    }

    private static void AddPlaced(
        ImmutableArray<PrefabPart> parts,
        Transform parent,
        ImmutableArray<PlacedPart>.Builder placed
    )
    {
        foreach (PrefabPart part in parts)
        {
            // The part's transform under its parent's, or its parent's place when the part has none.
            Transform at = part
                .Components.OfType<Transform>()
                .Select(local => local.Under(parent))
                .FirstOrDefault(parent);
            placed.Add(new PlacedPart(part, at));
            AddPlaced(part.Children, at, placed);
        }
    }
}

/// <summary>
/// One part of a prefab and where it goes in the world (<see cref="Prefab.Place"/>).
/// </summary>
public readonly record struct PlacedPart(PrefabPart Part, Transform At);

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

    /// <summary>
    /// The prefab that the part places, whose parts are the first of <see cref="Children"/>; none when it places
    /// none.
    /// </summary>
    public Handle<Prefab> Placed { get; init; }

    public ImmutableArray<PrefabPart> Children { get; init; } = [];
}
