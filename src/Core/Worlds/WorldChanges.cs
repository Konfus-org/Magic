using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using Magic.Ecs;
using Magic.Files;
using Magic.Files.Types;

namespace Magic.Worlds;

/// <summary>
/// Changes to the world's entities, each with what it was before and what it is after, as plain data: what a save holds
/// against the domains' files. It finds itself from the world as it is and makes itself on a world; <see cref="Invert"/>
/// gives the changes that undo it.
/// </summary>
internal readonly record struct WorldChanges(ImmutableArray<EntityChange> Entities)
{
    /// <summary>
    /// <see cref="IEcs.Remove{T}"/>, for a component whose type is known only at run time.
    /// </summary>
    private static readonly MethodInfo _removeComponent =
        typeof(IEcs).GetMethod(nameof(IEcs.Remove))
        ?? throw new MissingMethodException(nameof(IEcs), nameof(IEcs.Remove));

    /// <summary>
    /// What changed in <paramref name="ecs"/> against the domains' files: each placed entity the files,
    /// <paramref name="authored"/>, make otherwise, or that is gone, then every entity no domain placed. A placed entity
    /// <paramref name="authored"/> doesn't hold is left out. Main thread.
    /// </summary>
    public WorldChanges(
        IEcs ecs,
        IReadOnlyDictionary<EntityKey, Entity> placed,
        IReadOnlyDictionary<EntityKey, ImmutableArray<IComponent>> authored
    )
        : this(Find(ecs, placed, authored)) { }

    /// <summary>
    /// The changes that undo these: in reverse order, a made entity destroyed, a destroyed one made again, and each change
    /// back to what it was.
    /// </summary>
    public WorldChanges Invert()
    {
        ImmutableArray<EntityChange>.Builder inverted = ImmutableArray.CreateBuilder<EntityChange>(Entities.Length);
        for (int i = Entities.Length - 1; i >= 0; i--)
        {
            EntityChange change = Entities[i];
            EntityChangeKind kind = change.Kind switch
            {
                EntityChangeKind.Create => EntityChangeKind.Destroy,
                EntityChangeKind.Destroy => EntityChangeKind.Create,
                EntityChangeKind.Change => EntityChangeKind.Change,
                _ => change.Kind,
            };
            inverted.Add(new EntityChange(change.Key, kind, change.After, change.Before));
        }

        return new WorldChanges(inverted.MoveToImmutable());
    }

    /// <summary>
    /// Makes these changes on <paramref name="ecs"/> in order. A made entity a domain placed takes its key in
    /// <paramref name="placed"/>; a change to an entity no longer there is skipped. Main thread.
    /// </summary>
    public void Apply(IEcs ecs, Dictionary<EntityKey, Entity> placed)
    {
        Dictionary<EntityKey, Entity> found = new(placed);
        foreach (Entity entity in FindAll(ecs))
        {
            found.TryAdd(new EntityKey(Handle<Domain>.None, Handle<Prefab>.None, entity.Id), entity);
        }

        foreach (EntityChange change in Entities)
        {
            if (change.Kind == EntityChangeKind.Create)
            {
                Entity made = ecs.Create(change.After.AsSpan());
                found[change.Key] = made;
                if (change.Key.Domain != Handle<Domain>.None)
                    placed[change.Key] = made;

                continue;
            }

            if (!found.TryGetValue(change.Key, out Entity entity) || !ecs.IsAlive(entity))
                continue;

            if (change.Kind == EntityChangeKind.Destroy)
            {
                ecs.Destroy(entity);

                continue;
            }

            ecs.Set(entity, change.After.AsSpan());
            RemoveLeft(ecs, entity, change);
        }
    }

    private static ImmutableArray<EntityChange> Find(
        IEcs ecs,
        IReadOnlyDictionary<EntityKey, Entity> placed,
        IReadOnlyDictionary<EntityKey, ImmutableArray<IComponent>> authored
    )
    {
        ImmutableArray<EntityChange>.Builder changes = ImmutableArray.CreateBuilder<EntityChange>();
        foreach ((EntityKey key, Entity entity) in placed)
        {
            if (!authored.TryGetValue(key, out ImmutableArray<IComponent> before))
                continue;

            if (!ecs.IsAlive(entity))
                changes.Add(new EntityChange(key, EntityChangeKind.Destroy, before, []));
            else if (Compare(key, before, [.. ecs.GetComponents(entity)]) is { } change)
                changes.Add(change);
        }

        HashSet<Entity> owned = [.. placed.Values];
        foreach (Entity entity in FindAll(ecs))
        {
            if (owned.Contains(entity))
                continue;

            EntityKey key = new(Handle<Domain>.None, Handle<Prefab>.None, entity.Id);
            changes.Add(new EntityChange(key, EntityChangeKind.Create, [], [.. ecs.GetComponents(entity)]));
        }

        return changes.ToImmutable();
    }

    /// <summary>
    /// How <paramref name="after"/> differs from <paramref name="before"/>: the components of each that the other doesn't
    /// hold as they are; none when they hold the same.
    /// </summary>
    private static EntityChange? Compare(
        EntityKey key,
        ImmutableArray<IComponent> before,
        ImmutableArray<IComponent> after
    )
    {
        ImmutableArray<IComponent> was = [.. before.Where(component => !after.Contains(component))];
        ImmutableArray<IComponent> now = [.. after.Where(component => !before.Contains(component))];

        return was.IsEmpty && now.IsEmpty ? null : new EntityChange(key, EntityChangeKind.Change, was, now);
    }

    private static ImmutableArray<Entity> FindAll(IEcs ecs)
    {
        ImmutableArray<Entity>.Builder all = ImmutableArray.CreateBuilder<Entity>();
        foreach (EcsChunk chunk in ecs.Query())
        {
            all.AddRange(chunk.Entities);
        }

        return all.ToImmutable();
    }

    /// <summary>
    /// Takes off <paramref name="entity"/> each component <paramref name="change"/> has before and not after.
    /// </summary>
    private static void RemoveLeft(IEcs ecs, Entity entity, EntityChange change)
    {
        foreach (IComponent was in change.Before)
        {
            if (!change.After.Any(component => component.GetType() == was.GetType()))
                _removeComponent.MakeGenericMethod(was.GetType()).Invoke(ecs, [entity]);
        }
    }
}

/// <summary>
/// One entity's change: which, how, and its components before and after it. A change holds only the components that
/// differ: one only before is taken off, one only after is put on.
/// </summary>
internal readonly record struct EntityChange(
    EntityKey Key,
    EntityChangeKind Kind,
    ImmutableArray<IComponent> Before,
    ImmutableArray<IComponent> After
);

internal enum EntityChangeKind
{
    /// <summary>
    /// Made: nothing before, all of it after.
    /// </summary>
    Create,

    /// <summary>
    /// Changed: what differs, before and after.
    /// </summary>
    Change,

    /// <summary>
    /// Destroyed: all of it before, nothing after.
    /// </summary>
    Destroy,
}

/// <summary>
/// Which entity, the same in every run: one a domain placed is that domain, the prefab it placed, and the part of it, in
/// the order the prefab's parts spawn; one made as the game ran has no domain or prefab, and its slot as
/// <paramref name="Part"/>.
/// </summary>
internal readonly record struct EntityKey(Handle<Domain> Domain, Handle<Prefab> Prefab, int Part);
