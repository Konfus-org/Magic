using System.Collections.Immutable;
using Magic.Assets;
using Magic.Assets.Types;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Gems;
using Magic.Projects;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.Pipelines;

/// <summary>
/// First of a run: everything up to <see cref="Startup"/>.
/// </summary>
internal readonly record struct AppStart(Project Project);

/// <summary>
/// While <see cref="AppStart"/> runs: the gems were found and aren't yet made, so what must be there for their constructors
/// is made ready.
/// </summary>
internal readonly record struct GemsFound(GemRegistry Registry);

/// <summary>
/// The frames, for as long as the run goes on.
/// </summary>
internal readonly record struct AppLoop;

/// <summary>
/// After the last frame, with the gems still whole, before <see cref="Shutdown"/>. Awaited, as <see cref="Startup"/> is.
/// </summary>
internal readonly record struct RunEnding;

/// <summary>
/// Last of a run: everything after <see cref="Shutdown"/>.
/// </summary>
internal readonly record struct AppEnd;

/// <summary>
/// Once, every gem made, before the first frame. Awaited: frames don't run until every one has ended, though what it
/// awaits does (<see cref="ServicePump"/>).
/// </summary>
public readonly record struct Startup(Project Project);

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

/// <summary>
/// What keeps the host going, before each frame and while <see cref="Startup"/> or <see cref="Shutdown"/> are awaited, when
/// no frames run: what was posted to the main thread, the terminal, the assets. A gem whose work outside the frames must
/// go on then too, such as a window's, joins here.
/// </summary>
public readonly record struct ServicePump;

/// <summary>
/// First in a frame, with the frame's messages: for what the frame's work reads, such as input.
/// </summary>
public readonly record struct BeginUpdate(Frame Frame);

/// <summary>
/// Once a frame, for the game's work.
/// </summary>
public readonly record struct Update(Frame Frame);

/// <summary>
/// Work that is deterministic, independent of the frame rate, such as physics: after
/// <see cref="Update"/>, as many times a frame as the clock owes, up to its limit, each with one step's
/// clocks and no messages.
/// </summary>
public readonly record struct FixedUpdate(Frame Frame);

/// <summary>
/// What follows from the frame's work: transforms, listeners, what goes back to where it came from.
/// </summary>
public readonly record struct LateUpdate(Frame Frame);

/// <summary>
/// Drawing the frame.
/// </summary>
public readonly record struct Render(Frame Frame);

/// <summary>
/// Last in a frame's work, before the wait for the next frame.
/// </summary>
public readonly record struct EndUpdate(Frame Frame);

/// <summary>
/// Once, after the last frame, before the gems end, last gem first. Awaited, as <see cref="Startup"/> is.
/// </summary>
public readonly record struct Shutdown(bool IsExiting);
