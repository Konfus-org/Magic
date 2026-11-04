using System.Collections.Immutable;
using Magic.Assets;
using Magic.Assets.Types;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Utils;

namespace Magic.Worlds;

/// <summary>
/// A domain opening, under the world's one change at a time: read, then, for a replace, the loading screen shown and the
/// world cleared; then brought in, and the loading screen hidden. A gem's runs after it is open, or
/// <paramref name="Error"/> says why it doesn't, which leaves the world as it was.
/// </summary>
/// <param name="Domain">What opens.</param>
/// <param name="Mode">How it opens.</param>
/// <param name="Content">What it is in the world: the one already there, when it is open beside the others.</param>
/// <param name="Coming">Its entities, once read.</param>
/// <param name="Progress">What is told how far it is.</param>
/// <param name="Error">Why it doesn't open; empty until then.</param>
public readonly record struct WorldOpen(
    Handle<Domain> Domain,
    DomainOpenMode Mode,
    WorldContent Content,
    ImmutableArray<EntityTemplate> Coming,
    IProgress<LoadProgress>? Progress,
    string Error
);

/// <summary>
/// A saved world going back in place of everything in the world, under its one change at a time. A gem's runs after Core's.
/// </summary>
public readonly record struct WorldRestore(WorldState State, IProgress<LoadProgress>? Progress);

/// <summary>
/// A <typeparamref name="T"/> going into the world: what can spawn one joins, and fills in <paramref name="Entities"/>, the
/// entities to make for it, placed at <paramref name="At"/>. Left as it is, nothing can spawn one.
/// </summary>
public readonly record struct WorldSpawn<T>(T Asset, Transform At, ImmutableArray<EntityTemplate> Entities)
    where T : IAsset<T>;

/// <summary>
/// Content entering the world, once Core has made its entities into <paramref name="Arrived"/>: each gem loads its part of it, such as the files it needs, in
/// gem order, awaited, ending once it can be shown. To react to entities coming and going wherever they came from, join
/// <see cref="EntitiesChangeList"/>, rather than build the same thing from both.
/// </summary>
public readonly record struct WorldLoad(
    WorldContent Content,
    ImmutableArray<EntityTemplate> Coming,
    ImmutableArray<Entity> Arrived,
    IProgress<LoadProgress>? Progress
);

/// <summary>
/// Content leaving the world: each gem lets its part go, last gem first, awaited, and then Core destroys its entities.
/// </summary>
public readonly record struct WorldUnload(
    WorldContent Content,
    ImmutableArray<Entity> Leaving,
    IProgress<LoadProgress>? Progress
);
