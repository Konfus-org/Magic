using System.Collections.Immutable;
using Magic.Assets;
using Magic.Assets.Types;
using Magic.Ecs;
using Magic.Ecs.Components;

namespace Magic.Worlds;

/// <summary>
/// A prefab a domain places, and where.
/// </summary>
internal readonly record struct DomainPlace(Handle<Prefab> Prefab, Transform At);

/// <summary>
/// Finds what is in the world, and says where entities came from: functions over the ECS and plain data.
/// </summary>
internal static class WorldEntities
{
    public static ImmutableArray<Entity> FindAll(IEcs ecs)
    {
        ImmutableArray<Entity>.Builder all = ImmutableArray.CreateBuilder<Entity>();
        foreach (EcsChunk chunk in ecs.Query())
        {
            foreach (Entity entity in chunk.Entities)
            {
                all.Add(entity);
            }
        }

        return all.ToImmutable();
    }

    /// <summary>
    /// Every entity, as its components.
    /// </summary>
    public static ImmutableArray<EntityTemplate> GetTemplates(IEcs ecs)
    {
        ImmutableArray<Entity> all = FindAll(ecs);
        ImmutableArray<EntityTemplate>.Builder templates = ImmutableArray.CreateBuilder<EntityTemplate>(all.Length);
        foreach (Entity entity in all)
        {
            templates.Add(new EntityTemplate([.. ecs.GetComponents(entity)]));
        }

        return templates.MoveToImmutable();
    }

    /// <summary>
    /// The entities made as the game ran rather than placed, but its globals: what goes as a domain replaces the world.
    /// </summary>
    public static ImmutableArray<Entity> FindRuntime(IEcs ecs)
    {
        ImmutableArray<Entity>.Builder runtime = ImmutableArray.CreateBuilder<Entity>();
        foreach (Entity entity in FindAll(ecs))
        {
            bool isGlobal = ecs.Has<Tags>(entity) && ecs.Get<Tags>(entity).Has(Tag.Global);
            if (!isGlobal && !ecs.Has<Origin>(entity))
                runtime.Add(entity);
        }

        return runtime.ToImmutable();
    }

    /// <summary>
    /// The entities of the content <paramref name="id"/>.
    /// </summary>
    public static ImmutableArray<Entity> FindOf(IEcs ecs, ulong id)
    {
        ImmutableArray<Entity>.Builder found = ImmutableArray.CreateBuilder<Entity>();
        foreach ((ReadOnlySpan<Entity> entities, Span<Origin> origins) in ecs.Query<Origin>())
        {
            for (int i = 0; i < entities.Length; i++)
            {
                if (origins[i].Content == id)
                    found.Add(entities[i]);
            }
        }

        return found.ToImmutable();
    }

    /// <summary>
    /// The entities that came from one placing of a prefab, as <paramref name="origin"/> says.
    /// </summary>
    public static ImmutableArray<Entity> FindOf(IEcs ecs, Origin origin)
    {
        ImmutableArray<Entity>.Builder found = ImmutableArray.CreateBuilder<Entity>();
        foreach ((ReadOnlySpan<Entity> entities, Span<Origin> origins) in ecs.Query<Origin>())
        {
            for (int i = 0; i < entities.Length; i++)
            {
                if (origins[i] == origin)
                    found.Add(entities[i]);
            }
        }

        return found.ToImmutable();
    }

    /// <summary>
    /// Each place <paramref name="prefab"/> was put, anywhere in the world.
    /// </summary>
    public static ImmutableArray<Origin> FindOrigins(IEcs ecs, Handle<Prefab> prefab)
    {
        HashSet<Origin> found = [];
        foreach ((ReadOnlySpan<Entity> _, Span<Origin> origins) in ecs.Query<Origin>())
        {
            foreach (Origin origin in origins)
            {
                if (origin.Prefab == prefab)
                    found.Add(origin);
            }
        }

        return [.. found];
    }

    /// <summary>
    /// Each prefab <paramref name="content"/> placed, and where. The player's own has no prefab, so it isn't one.
    /// </summary>
    public static ImmutableArray<Origin> FindOrigins(IEcs ecs, WorldContent content)
    {
        HashSet<Origin> found = [];
        foreach ((ReadOnlySpan<Entity> _, Span<Origin> origins) in ecs.Query<Origin>())
        {
            foreach (Origin origin in origins)
            {
                if (origin.Content == content.Id && origin.Prefab != Handle<Prefab>.None)
                    found.Add(origin);
            }
        }

        return [.. found];
    }

    /// <summary>
    /// <paramref name="entities"/>, each saying it is <paramref name="content"/>'s, from <paramref name="prefab"/>, put at
    /// <paramref name="at"/>.
    /// </summary>
    public static ImmutableArray<EntityTemplate> WithOrigin(
        ImmutableArray<EntityTemplate> entities,
        Transform at,
        Handle<Prefab> prefab,
        WorldContent content
    )
    {
        Origin origin = new()
        {
            Domain = content.Domain,
            Content = content.Id,
            Prefab = prefab,
            At = at,
        };
        ImmutableArray<EntityTemplate>.Builder placed = ImmutableArray.CreateBuilder<EntityTemplate>(entities.Length);
        foreach (EntityTemplate entity in entities)
        {
            placed.Add(new EntityTemplate(entity.Components.Add(origin)));
        }

        return placed.MoveToImmutable();
    }

    /// <summary>
    /// <paramref name="components"/>, tagged global; throws when their tags have no room for that.
    /// </summary>
    public static ImmutableArray<IComponent> WithGlobalTag(ReadOnlySpan<IComponent> components)
    {
        ImmutableArray<IComponent>.Builder tagged = ImmutableArray.CreateBuilder<IComponent>(components.Length + 1);
        bool hasTags = false;
        foreach (IComponent component in components)
        {
            hasTags |= component is Tags;
            tagged.Add(component is Tags tags ? AddGlobalTag(tags) : component);
        }

        if (!hasTags)
            tagged.Add(AddGlobalTag(default));

        return tagged.ToImmutable();
    }

    public static void ThrowIfNoRoom(int count, Span<Entity> created)
    {
        if (created.Length != count)
        {
            throw new ArgumentException(
                $"A batch makes one entity per component: {count} components, room for {created.Length} entities.",
                nameof(created)
            );
        }
    }

    /// <summary>
    /// Where <paramref name="domain"/> places each of its prefabs: its globals first, then its chunks.
    /// </summary>
    public static ImmutableArray<DomainPlace> GetPlaces(Domain domain)
    {
        ImmutableArray<DomainPlace>.Builder places = ImmutableArray.CreateBuilder<DomainPlace>(
            domain.Globals.Length + domain.Chunks.Length
        );
        foreach (Handle<Prefab> global in domain.Globals)
        {
            places.Add(new DomainPlace(global, new Transform()));
        }

        foreach (Chunk chunk in domain.Chunks)
        {
            places.Add(new DomainPlace(chunk.Prefab, chunk.At));
        }

        return places.MoveToImmutable();
    }

    private static Tags AddGlobalTag(Tags tags)
    {
        if (tags.Has(Tag.Global) || tags.Add(Tag.Global))
            return tags;

        throw new ArgumentException(
            $"The entity's tags have no room to tag it global: an entity has at most {Tags.Capacity}.",
            nameof(tags)
        );
    }
}
