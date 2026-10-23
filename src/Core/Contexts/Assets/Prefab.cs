using Magic.Contexts.Components;
using Magic.Contexts.ECS;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.Contexts.Assets;

/// <summary>
/// A bundle of entities, spawned together wherever it is put (<see cref="World.Spawn"/>). It is a tree of parts, each
/// spawned as one entity, as the gem that reads <c>.prefab</c> files made it of one: a prefab it places is a part whose
/// children are that prefab's parts. Positions are relative to where it is put.
/// </summary>
public sealed class Prefab : IAsset<Prefab>, ISpawnable
{
    public static IReadOnlyList<string> Extensions { get; } = [".prefab"];

    public Handle<Prefab> Handle { get; init; }

    /// <summary>
    /// Its top parts, each with the parts under it.
    /// </summary>
    public required IReadOnlyList<PrefabPart> Parts { get; init; }

    /// <summary>
    /// Makes an entity of each part, a part before the parts under it, each with a copy of its components and its
    /// transform placed under its parent's, the top parts' under <paramref name="at"/>; then hands each part's scripts,
    /// with their values, to the world (<see cref="World.Attach"/>), which starts them in the next frame, on the whole
    /// prefab. A part without a transform is where its parent is.
    /// </summary>
    public Entity[] Spawn(World world, Transform at)
    {
        List<(Entity Entity, PrefabPart Part)> made = [];
        Create(world, Parts, at, made);
        Entity[] entities = new Entity[made.Count];
        for (int i = 0; i < entities.Length; i++)
        {
            entities[i] = made[i].Entity;
            foreach (PrefabScript script in made[i].Part.Scripts)
            {
                world.Attach(entities[i], script);
            }
        }

        return entities;
    }

    /// <summary>
    /// Where its first part tagged <paramref name="tag"/> is, a part before the parts under it, in the prefab's own
    /// space; null when no part is.
    /// </summary>
    public Transform? FindWith(Tag tag) => PlaceIn(Parts, new Transform(), tag);

    private static void Create(
        World world,
        IReadOnlyList<PrefabPart> parts,
        Transform parent,
        List<(Entity Entity, PrefabPart Part)> made)
    {
        foreach (PrefabPart part in parts)
        {
            Transform placed = part.PlaceUnder(parent);
            IComponent[] components = new IComponent[part.Components.Count];
            for (int i = 0; i < components.Length; i++)
            {
                components[i] = part.Components[i] is Transform ? placed : part.Components[i];
            }

            made.Add((world.Create(components), part));
            Create(world, part.Children, placed, made);
        }
    }

    private static Transform? PlaceIn(IReadOnlyList<PrefabPart> parts, Transform parent, Tag tag)
    {
        foreach (PrefabPart part in parts)
        {
            Transform placed = part.PlaceUnder(parent);
            foreach (IComponent component in part.Components)
            {
                if (component is Tags tags && tags.Has(tag)) return placed;
            }

            if (PlaceIn(part.Children, placed, tag) is { } under) return under;
        }

        return null;
    }
}

/// <summary>
/// A script as a <see cref="PrefabPart"/> holds it: the <see cref="Script"/> that declares it, and the values the
/// prefab gives it, each a public property's name and its value read as the property's type. A script is made for an
/// entity, so a part cannot hold one made (<see cref="IScript"/>); it holds what each entity spawned from it has its
/// own copy made from.
/// </summary>
public sealed record PrefabScript(Script Script, IReadOnlyDictionary<string, object?> Values);

/// <summary>
/// One part of a <see cref="Prefab"/>, spawned as one entity: its components, its scripts and the parts under it, whose
/// transforms are relative to its own. Each entity spawned from it is given a copy of its components and of its
/// scripts, and nothing changes a part once it is made, as a prefab is shared by everything that spawns it.
/// </summary>
public sealed class PrefabPart
{
    /// <summary>
    /// What messages call it.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Its components, each of its own type, its tags among them as a <see cref="Tags"/>.
    /// </summary>
    public IReadOnlyList<IComponent> Components { get; init; } = [];

    /// <summary>
    /// Its scripts, each with the values it is given; each entity spawned from it runs its own copy of each
    /// (<see cref="World.Attach"/>).
    /// </summary>
    public IReadOnlyList<PrefabScript> Scripts { get; init; } = [];

    public IReadOnlyList<PrefabPart> Children { get; init; } = [];

    /// <summary>
    /// Where it is under <paramref name="parent"/>, its parent's place: its transform placed there, or the parent's
    /// place when it has none.
    /// </summary>
    public Transform PlaceUnder(Transform parent)
    {
        foreach (IComponent component in Components)
        {
            if (component is Transform local) return local.Under(parent);
        }

        return parent;
    }
}
