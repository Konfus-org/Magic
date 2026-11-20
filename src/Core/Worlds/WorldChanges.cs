using System.Collections.Concurrent;
using System.Collections.Immutable;
using Magic.Ecs;

namespace Magic.Worlds;

/// <summary>
/// What differs in one entity that a domain placed: the components it has now, and the types that it lost.
/// </summary>
/// <param name="Set">The components that the entity gained or changed, by type name.</param>
/// <param name="Removed">The type names of the components that the entity lost.</param>
public sealed record EntityChange(IReadOnlyDictionary<string, IComponent> Set, ImmutableArray<string> Removed);

/// <summary>
/// How a world differs from the <see cref="WorldState"/> that the files of its domains make, in memory: no JSON is
/// made to find it or to apply it.
/// </summary>
/// <remarks>
/// The entities that a domain placed are by their key. A component is the same when its type says it is equal, so a
/// type with an equality that is too strict only adds a value that did not change.
/// </remarks>
/// <param name="Changed">The placed entities that the base holds and that differ, by key.</param>
/// <param name="Added">The placed entities that the base lacks, by key, with all their components.</param>
/// <param name="Gone">The keys of the placed entities that the base holds and the world lacks.</param>
/// <param name="Made">The entities without an owner.</param>
/// <param name="IsPaused">Whether the world is paused.</param>
/// <param name="TimeScale">How fast the world runs.</param>
public sealed record WorldChanges(
    IReadOnlyDictionary<string, EntityChange> Changed,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, IComponent>> Added,
    ImmutableArray<string> Gone,
    ImmutableArray<IReadOnlyDictionary<string, IComponent>> Made,
    bool IsPaused,
    float TimeScale
)
{
    private static readonly IReadOnlyDictionary<string, IComponent> _noComponents =
        new Dictionary<string, IComponent>();

    /// <summary>
    /// How <paramref name="now"/> differs from <paramref name="was"/>. Safe from any thread; it compares the entities
    /// in parallel.
    /// </summary>
    internal static WorldChanges Between(WorldState was, WorldState now)
    {
        KeyValuePair<string, IReadOnlyDictionary<string, IComponent>>[] entities = [.. now.Placed];
        EntityChange?[] changes = new EntityChange?[entities.Length];
        bool[] isAdded = new bool[entities.Length];
        Parallel.ForEach(
            Partitioner.Create(0, entities.Length),
            range =>
            {
                for (int i = range.Item1; i < range.Item2; i++)
                {
                    if (was.Placed.TryGetValue(entities[i].Key, out IReadOnlyDictionary<string, IComponent>? before))
                        changes[i] = Compare(before, entities[i].Value);
                    else
                        isAdded[i] = true;
                }
            }
        );

        Dictionary<string, EntityChange> changed = [];
        Dictionary<string, IReadOnlyDictionary<string, IComponent>> added = [];
        for (int i = 0; i < entities.Length; i++)
        {
            if (isAdded[i])
                added.Add(entities[i].Key, entities[i].Value);
            else if (changes[i] is { } change)
                changed.Add(entities[i].Key, change);
        }

        ImmutableArray<string>.Builder gone = ImmutableArray.CreateBuilder<string>();
        foreach (string key in was.Placed.Keys)
        {
            if (!now.Placed.ContainsKey(key))
                gone.Add(key);
        }

        return new WorldChanges(changed, added, gone.ToImmutable(), now.Made, now.IsPaused, now.TimeScale);
    }

    /// <summary>
    /// The entities that <paramref name="state"/> makes with these changes made on it: the placed first, then the
    /// added, then the made.
    /// </summary>
    /// <remarks>
    /// A change to an entity or a component that <paramref name="state"/> does not hold, because its files no longer
    /// make it, is skipped. Safe from any thread.
    /// </remarks>
    internal ImmutableArray<ImmutableArray<IComponent>> ApplyTo(WorldState state)
    {
        HashSet<string> gone = [.. Gone];
        int destroyed = 0;
        foreach (string key in gone)
        {
            if (state.Placed.ContainsKey(key))
                destroyed++;
        }

        ImmutableArray<ImmutableArray<IComponent>>.Builder entities = ImmutableArray.CreateBuilder<
            ImmutableArray<IComponent>
        >(state.Placed.Count - destroyed + Added.Count + Made.Length);
        foreach ((string key, IReadOnlyDictionary<string, IComponent> components) in state.Placed)
        {
            if (gone.Contains(key))
                continue;

            entities.Add(
                Changed.TryGetValue(key, out EntityChange? change) ? Apply(components, change) : [.. components.Values]
            );
        }

        foreach (IReadOnlyDictionary<string, IComponent> components in Added.Values)
        {
            entities.Add([.. components.Values]);
        }

        foreach (IReadOnlyDictionary<string, IComponent> components in Made)
        {
            entities.Add([.. components.Values]);
        }

        return entities.MoveToImmutable();
    }

    private static EntityChange? Compare(
        IReadOnlyDictionary<string, IComponent> was,
        IReadOnlyDictionary<string, IComponent> now
    )
    {
        Dictionary<string, IComponent>? set = null;
        foreach ((string type, IComponent component) in now)
        {
            if (!was.TryGetValue(type, out IComponent? before) || !before.Equals(component))
                (set ??= [])[type] = component;
        }

        ImmutableArray<string>.Builder? removed = null;
        foreach (string type in was.Keys)
        {
            if (!now.ContainsKey(type))
                (removed ??= ImmutableArray.CreateBuilder<string>()).Add(type);
        }

        return set is null && removed is null
            ? null
            : new EntityChange(set ?? _noComponents, removed?.ToImmutable() ?? []);
    }

    private static ImmutableArray<IComponent> Apply(
        IReadOnlyDictionary<string, IComponent> components,
        EntityChange change
    )
    {
        ImmutableArray<IComponent>.Builder result = ImmutableArray.CreateBuilder<IComponent>(
            components.Count + change.Set.Count
        );
        foreach ((string type, IComponent component) in components)
        {
            if (!change.Removed.Contains(type) && !change.Set.ContainsKey(type))
                result.Add(component);
        }

        foreach (IComponent component in change.Set.Values)
        {
            result.Add(component);
        }

        return result.ToImmutable();
    }
}
