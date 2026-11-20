using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.InteropServices;
using Magic.Assets;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Events;
using Magic.Projects;
using Magic.Threading;
using Magic.Utils;

namespace Magic.Worlds;

/// <summary>
/// A prefab a domain places, and where.
/// </summary>
internal readonly record struct DomainPlace(Handle<Prefab> Prefab, Transform At);

/// <summary>
/// A prefab a domain places, where, and the prefab as it was read.
/// </summary>
internal readonly record struct PlacedPrefab(DomainPlace Place, Prefab Prefab);

/// <summary>
/// A domain, as it was read, and the prefabs it places.
/// </summary>
internal readonly record struct DomainRead(Handle<Domain> Domain, Domain Asset, ImmutableArray<PlacedPrefab> Prefabs);

/// <summary>
/// The game world: what is in it, the one place to load content into it, and the way to its entities.
/// </summary>
/// <remarks>
/// <para>
/// Each entity call goes to the ECS that a gem gives. Content comes and goes one load at a time. Each entity that a
/// domain placed has its <see cref="Owner"/>, for saves and for when the domain opens again.
/// </para>
/// <para>
/// When the run starts, the world puts back the world that the last run kept, or opens the entry point. It does not
/// wait for the load, because frames run while it loads. When the run ends for a restart, it keeps the world for the
/// next run. It changes when the assets say that something changed.
/// </para>
/// <para>
/// Make entity calls on the game thread. Make the other calls on any thread.
/// </para>
/// </remarks>
public sealed class World : Disposable, IEcs, IWorldClock
{
    /// <summary>
    /// How long the cover takes to fade in or out.
    /// </summary>
    private const double CoverFadeSec = 0.4; // PROTO

    /// <summary>
    /// How long the cover waits at most for the gems to settle the world that arrived: until both this time passed and
    /// this many frames were drawn, so a gem that never settles ends the wait, and a low frame rate alone doesn't.
    /// </summary>
    private const double SettleLimitSec = 5; // PROTO

    private const uint SettleLimitFrames = 120; // PROTO

    private const string NoSaveSerializer =
        "no gem the project lists provides the save serializer (ISaveSerializer), so nothing is saved and a restart keeps no world state.";

    private readonly AssetManager _assets;

    private readonly EventCoordinator _events;

    private readonly Threads _threads;

    private readonly App _app;

    /// <summary>
    /// Held while work on the world runs, so it runs in turn.
    /// </summary>
    private readonly SemaphoreSlim _turn;

    /// <summary>
    /// Each open domain and the prefabs it placed, as they were read. Game thread.
    /// </summary>
    /// <remarks>
    /// A save, and a domain that opens again, compare the entities with them. They are held, so the assets read them
    /// again when their files change.
    /// </remarks>
    private readonly Dictionary<Handle<Domain>, DomainRead> _read = [];

    /// <summary>
    /// The ECS a gem provides, from when it is made until it goes; null while none does.
    /// </summary>
    private IEcs? _ecs;

    /// <summary>
    /// The gem that writes and reads the bytes of a save, null while no gem the project lists provides one. Game thread.
    /// </summary>
    private ISaveSerializer? _serializer;

    /// <summary>
    /// The debug UI a gem provides, from when it is made until it goes; null while none does.
    /// </summary>
    private IDebugUI? _debugUI;

    /// <summary>
    /// What the load that runs loads, as the debug UI shows it each frame; null while none runs. Any thread.
    /// </summary>
    private volatile string? _loadingHeader;

    /// <summary>
    /// What the load that runs does now, indented as the debug UI shows it under <see cref="_loadingHeader"/>; null
    /// until the load's first step. Any thread.
    /// </summary>
    private volatile string? _loadingStep;

    /// <summary>
    /// What a replace waits on frame by frame. Game thread.
    /// </summary>
    /// <remarks>
    /// <see cref="Pump"/> completes it when the next frame starts, and replaces it for the frame after.
    /// </remarks>
    private TaskCompletionSource _frameStarting = new();

    /// <summary>
    /// The gems that hook into domain loading, in the order they were made. Game thread.
    /// </summary>
    private ImmutableArray<IHookIntoDomainLoading> _hooks = [];

    /// <summary>
    /// The gems that write what they hold into the components for a save, in the order they were made. Game thread.
    /// </summary>
    private ImmutableArray<IHookIntoWorldSave> _saveHooks = [];

    /// <summary>
    /// The last save's write of the world it took, which runs on a worker and reads the sets of script values of that
    /// world. Game thread.
    /// </summary>
    private Task _saving = Task.CompletedTask;

    internal World(AssetManager assets, EventCoordinator events, Threads threads, App app)
    {
        _assets = assets;
        _events = events;
        _threads = threads;
        _app = app;
        _turn = Track(new SemaphoreSlim(1, 1));
    }

    /// <summary>
    /// Whether a domain or a saved world replaces what is in the world.
    /// </summary>
    /// <remarks>
    /// It is true from when the load starts to fade the world out until the cover faded into what replaced it.
    /// </remarks>
    public bool IsLoading { get; private set; }

    /// <summary>
    /// How much a cover of one colour hides the window: 0 shows the window, 1 hides it.
    /// </summary>
    /// <remarks>
    /// It is 0 outside a load. During a load, the world that shows fades into the cover, the cover fades into the
    /// loading screen, the loading screen fades into the cover, the world changes behind the full cover, and the cover
    /// fades into the world when the gems settled. Read it on any thread.
    /// </remarks>
    public float CoverOpacity
    {
        get => Volatile.Read(ref field);
        private set => Volatile.Write(ref field, value);
    }

    /// <summary>
    /// Whether the time of the world stands still, with no fixed steps, from the next frame.
    /// </summary>
    /// <remarks>
    /// It is ignored while the loading screen shows, because the gems can need steps to get ready. While the cover is
    /// full, the time of the world stands still however it is set.
    /// </remarks>
    public bool IsPaused { get; set; }

    /// <summary>
    /// How fast the time of the world runs against real time, from the next frame.
    /// </summary>
    /// <remarks>
    /// A value below 0 is 0. It is ignored while the loading screen shows or the cover is full.
    /// </remarks>
    public float TimeScale
    {
        get;
        set => field = Math.Max(value, 0);
    } = 1;

    /// <summary>
    /// The domains open, in the order they came, without the loading screen.
    /// </summary>
    /// <remarks>
    /// While the loading screen shows, it is empty.
    /// </remarks>
    public ImmutableArray<Handle<Domain>> Domains { get; private set; } = [];

    /// <summary>
    /// The last frame whose hooks before Render ran, so that what it made of the world is ready for the render thread.
    /// </summary>
    /// <remarks>
    /// Set it on the game thread. Read it on any thread.
    /// </remarks>
    internal uint PublishedFrame
    {
        get => Volatile.Read(ref field);
        set => Volatile.Write(ref field, value);
    }

    /// <summary>
    /// The newest published frame that the render thread drew, or one after it.
    /// </summary>
    /// <remarks>
    /// Set it on the render thread. Read it on any thread.
    /// </remarks>
    internal uint DrawnFrame
    {
        get => Volatile.Read(ref field);
        set => Volatile.Write(ref field, value);
    }

    /// <summary>
    /// Whether a gem provides the ECS, so content can come into the world.
    /// </summary>
    private bool HasEcs => _ecs is not null;

    /// <summary>
    /// The ECS a gem provides; throws when none does.
    /// </summary>
    private IEcs Ecs =>
        _ecs
        ?? throw new InvalidOperationException(
            "No gem the project lists provides the ECS, so the world holds no entities."
        );

    /// <summary>
    /// Creates an entity with <paramref name="components"/> now.
    /// </summary>
    /// <remarks>
    /// An entity whose <see cref="Tags"/> hold <see cref="Tag.Global"/> outlives each domain that replaces the world.
    /// </remarks>
    public Entity Create(ReadOnlySpan<IComponent> components) => Ecs.Create(components);

    /// <inheritdoc/>
    public void CreateMany(ReadOnlySpan<ImmutableArray<IComponent>> entities, Span<Entity> created) =>
        Ecs.CreateMany(entities, created);

    /// <inheritdoc/>
    public void DestroyMany(ReadOnlySpan<Entity> entities) => Ecs.DestroyMany(entities);

    /// <summary>
    /// Returns the game as a save holds it, in the bytes of the save serializer: the open domains, and how the world differs
    /// from them. It holds the world's turn, so no other work on the world runs while it takes the snapshot.
    /// </summary>
    /// <remarks>
    /// It returns why not while the world loads or has no open domain, or when no gem provides the save serializer. The
    /// bytes name the types of their components and hold no instance, so a later run of the same gems can read them.
    /// </remarks>
    public async Task<Result<byte[]>> SaveAsync(CancellationToken cancel)
    {
        long saveStart = Metrics.WorldLoadDurations.Start();
        Task<Result<byte[]>> saving = await RunInTurnAsync(() => Task.FromResult(SaveAsync()), cancel)
            .ConfigureAwait(false);
        Result<byte[]> bytes = await saving.ConfigureAwait(false);
        RecordLoad(saveStart, "save");

        return bytes;
    }

    /// <summary>
    /// Puts the world back as the save <paramref name="bytes"/> hold it, in place of everything in the world, globals
    /// too.
    /// </summary>
    /// <remarks>
    /// The bytes are what <see cref="SaveAsync(CancellationToken)"/> gave. Its domains open as their files make them,
    /// with its changes made on them. It returns why not when no gem provides the save serializer, the serializer refuses the
    /// bytes, or one of its domains does not open. Then the world stays as it was.
    /// </remarks>
    public async Task<Result> RestoreAsync(byte[] bytes, IProgress<LoadProgress>? progress, CancellationToken cancel)
    {
        if (_serializer is not { } serializer)
            return Result.Failure(NoSaveSerializer);

        Result<SaveData> saved = serializer.Read(bytes);
        if (!saved.Ok)
            return Result.Failure(saved.Error);

        return await RestoreAsync(saved.Value, progress, cancel).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void Destroy(Entity entity) => Ecs.Destroy(entity);

    /// <inheritdoc/>
    public bool IsAlive(Entity entity) => Ecs.IsAlive(entity);

    /// <inheritdoc/>
    public void Set<T>(Entity entity, in T component)
        where T : unmanaged, IComponent => Ecs.Set(entity, in component);

    /// <inheritdoc/>
    public void Set(Entity entity, ReadOnlySpan<IComponent> components) => Ecs.Set(entity, components);

    /// <inheritdoc/>
    public ref readonly T Get<T>(Entity entity)
        where T : unmanaged, IComponent => ref Ecs.Get<T>(entity);

    /// <inheritdoc/>
    public ref T Mutate<T>(Entity entity)
        where T : unmanaged, IComponent => ref Ecs.Mutate<T>(entity);

    /// <inheritdoc/>
    public bool Has<T>(Entity entity)
        where T : unmanaged, IComponent => Ecs.Has<T>(entity);

    /// <inheritdoc/>
    public void Remove<T>(Entity entity)
        where T : unmanaged, IComponent => Ecs.Remove<T>(entity);

    /// <inheritdoc/>
    public IComponent[] GetComponents(Entity entity) => Ecs.GetComponents(entity);

    /// <inheritdoc/>
    public EcsQuery Query() => Ecs.Query();

    /// <inheritdoc/>
    public EcsQuery<T1> Query<T1>()
        where T1 : unmanaged, IComponent => Ecs.Query<T1>();

    /// <inheritdoc/>
    public EcsQuery<T1, T2> Query<T1, T2>()
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent => Ecs.Query<T1, T2>();

    /// <inheritdoc/>
    public EcsMutation<T1> Mutate<T1>()
        where T1 : unmanaged, IComponent => Ecs.Mutate<T1>();

    /// <inheritdoc/>
    public EcsMutation<T1, T2> Mutate<T1, T2>()
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent => Ecs.Mutate<T1, T2>();

    /// <inheritdoc/>
    public void MutateParallel<TState, T1>(in TState state, EcsChunkRun<TState, EcsMutableChunk<T1>> run)
        where T1 : unmanaged, IComponent => Ecs.MutateParallel(in state, run);

    /// <inheritdoc/>
    public void MutateParallel<TState, T1, T2>(in TState state, EcsChunkRun<TState, EcsMutableChunk<T1, T2>> run)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent => Ecs.MutateParallel(in state, run);

    /// <summary>
    /// Opens <paramref name="domain"/> in place of what is in the world but the globals, with the player at its spawn.
    /// </summary>
    /// <remarks>
    /// The loading screen shows until the domain is there. Workers read the domain first, so a domain that does not
    /// load leaves the world as it was. It returns why not when the domain does not load or no gem gives the ECS.
    /// </remarks>
    public async Task<Result> OpenAsync(
        Handle<Domain> domain,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        Result opened = await RunInTurnAsync(() => OpenDomainAsync(domain, progress, cancel), cancel)
            .ConfigureAwait(false);

        return opened.Ok ? opened : Result.Failure($"The domain does not open: {opened.Error}");
    }

    /// <summary>
    /// Loads <paramref name="prefab"/>, puts it in the world at <paramref name="at"/>, and returns its entities.
    /// </summary>
    /// <remarks>
    /// The caller destroys the entities. When a domain replaces the world, the entities that are still there go. It
    /// returns why not when the prefab does not load or no gem gives the ECS. For a single entity, use
    /// <see cref="Create"/>.
    /// </remarks>
    public async Task<Result<ImmutableArray<Entity>>> SpawnAsync(
        Handle<Prefab> prefab,
        Transform at,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        if (!HasEcs)
            return Result.Failure("Nothing spawns: no gem the project lists provides the ECS.");

        Result<Prefab> loaded = await _assets.LoadAsync(prefab, progress, cancel).ConfigureAwait(false);
        if (!loaded.Ok)
            return Result.Failure(loaded.Error);

        ImmutableArray<ImmutableArray<IComponent>> coming = GetEntities(loaded.Value, at);

        return Result.Success(
            await RunInTurnAsync(() => Task.FromResult(CreateEntities(coming)), cancel).ConfigureAwait(false)
        );
    }

    /// <summary>
    /// Puts the game back as <paramref name="saved"/> holds it, in place of all of the world, globals too.
    /// </summary>
    /// <remarks>
    /// Its domains are read together, then the world is replaced with them and the entities that
    /// <see cref="WorldChanges.ApplyTo"/> makes. It returns why not when one of its domains does not open. Then the world
    /// stays as it was.
    /// </remarks>
    internal async Task<Result> RestoreAsync(
        SaveData saved,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        long restoreStart = Metrics.WorldLoadDurations.Start();
        Result restored = await RunInTurnAsync(() => OpenSaveAsync(saved, progress, cancel), cancel)
            .ConfigureAwait(false);
        RecordLoad(restoreStart, "restore");

        return restored;
    }

    /// <summary>
    /// Takes the world as a save needs it, and has the save serializer write it: the open domains, and how the world differs
    /// from the entities that their files make, with the pause and the time scale.
    /// </summary>
    /// <remarks>
    /// The save hooks, the sweep of the kept script state and the copy of the components run on the game thread, before
    /// this returns. The change set, which compares the entities in memory, and the write run on workers. The write
    /// finishes inside the guard of <see cref="_saving"/>, because the components in the snapshot refer to script state
    /// that the next save frees. It returns why not while the world loads or has no open domain, or when no gem
    /// provides the save serializer. Call it on the game thread.
    /// </remarks>
    internal Task<Result<byte[]>> SaveAsync()
    {
        if (IsLoading || Domains.IsEmpty)
            return Task.FromResult<Result<byte[]>>(
                Result.Failure(IsLoading ? "the world is loading." : "no domain is open.")
            );

        if (_serializer is not { } serializer)
            return Task.FromResult<Result<byte[]>>(Result.Failure(NoSaveSerializer));

        RunSaveHooks();
        FreeKeptScriptState();

        ImmutableArray<Handle<Domain>> open = Domains;
        ImmutableArray<DomainRead> domains = [.. open.Select(domain => _read[domain])];
        WorldState now = GetState();

        Task<Result<byte[]>> saving = Task.Run(() =>
            serializer.Write(new SaveData(open, WorldChanges.Between(GetBaseState(domains), now)))
        );
        _saving = saving;

        return saving;
    }

    /// <summary>
    /// Changes the world for the assets read again, frees the material sets that no renderer holds, and lets a waiting
    /// replace continue.
    /// </summary>
    /// <remarks>
    /// The assets read again are the prefabs and domains that the take of the last frame heard. Call it on the game
    /// thread when each frame starts, before the hooks of the gems.
    /// </remarks>
    internal void Pump()
    {
        // Open again what places a prefab or domain the assets read again.
        ReadOnlySpan<AssetReloaded> reloaded = _events.GetTaken<AssetReloaded>();
        if (!reloaded.IsEmpty)
        {
            HashSet<ulong> changed = [];
            foreach (AssetReloaded asset in reloaded)
            {
                if (asset.Asset is Prefab or Domain)
                    changed.Add(asset.Id);
            }

            if (changed.Count > 0)
                ReopenAsync(changed, _app.Lifetime).FireAndForget();
        }

        if (_ecs is { } ecs)
            MaterialSet.SweepIfDue(ecs);

        DrawLoading();

        // Let a replace waiting for this frame go on.
        TaskCompletionSource starting = _frameStarting;
        _frameStarting = new TaskCompletionSource();
        starting.SetResult();
    }

    /// <summary>
    /// Has <paramref name="ecs"/> hold the world's entities, until the subscription is disposed. Game thread.
    /// </summary>
    internal Subscription Register(IEcs ecs)
    {
        _ecs = ecs;

        return new Subscription(() => _ecs = null);
    }

    /// <summary>
    /// Has <paramref name="serializer"/> write and read the bytes of the saves, until the subscription is disposed. Game
    /// thread.
    /// </summary>
    internal Subscription Register(ISaveSerializer serializer)
    {
        _serializer = serializer;

        return new Subscription(() => _serializer = null);
    }

    /// <summary>
    /// Has <paramref name="debugUI"/> show what a load does, until the subscription is disposed. Game thread.
    /// </summary>
    internal Subscription Register(IDebugUI debugUI)
    {
        _debugUI = debugUI;

        return new Subscription(() => _debugUI = null);
    }

    /// <summary>
    /// Has <paramref name="hooks"/> take part as domains replace the world, until the subscription is disposed.
    /// </summary>
    /// <remarks>
    /// Call it on the game thread.
    /// </remarks>
    internal Subscription Register(IHookIntoDomainLoading hooks)
    {
        _hooks = _hooks.Add(hooks);

        return new Subscription(() => _hooks = _hooks.Remove(hooks));
    }

    /// <summary>
    /// Has <paramref name="hooks"/> write what it holds into the components before each save, until the subscription
    /// is disposed.
    /// </summary>
    /// <remarks>
    /// Call it on the game thread.
    /// </remarks>
    internal Subscription Register(IHookIntoWorldSave hooks)
    {
        _saveHooks = _saveHooks.Add(hooks);

        return new Subscription(() => _saveHooks = _saveHooks.Remove(hooks));
    }

    /// <summary>
    /// Puts back the world as the last run saved it, <paramref name="restorePoint"/>, when that save is done.
    /// </summary>
    /// <remarks>
    /// The entry point opens when there is no save, when the save failed, with a warning, or when the save does not
    /// open, with an error. The entry point is that of the project, or the default when the project names none or its
    /// own does not open. With no ECS, nothing opens, with a warning.
    /// </remarks>
    internal async Task StartAsync(
        Task<Result<byte[]>>? restorePoint,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        if (restorePoint is not null)
        {
            Result<byte[]> saved = await restorePoint.ConfigureAwait(false);
            Result restored = saved.Ok
                ? await RestoreAsync(saved.Value, progress, cancel).ConfigureAwait(false)
                : saved;
            if (restored.Ok)
                return;

            if (saved.Ok)
                Log.Error($"The world goes back to the entry point: its save does not open: {restored.Error}");
            else
                Log.Warn($"The world goes back to the entry point: it was not saved: {saved.Error}");
        }

        if (!HasEcs)
        {
            Log.Warn("No gem the project lists provides the ECS, so the world holds no entities, and no domain opens.");

            return;
        }

        Handle<Domain> fallback = _assets.GetDefault<Domain>();
        Handle<Domain> entry = _app.Project?.EntryPoint ?? Handle<Domain>.None;
        Handle<Domain> first = entry == Handle<Domain>.None ? fallback : entry;
        Result opened = await OpenAsync(first, progress, cancel).ConfigureAwait(false);
        if (opened.Ok)
            return;

        Log.Error(opened.Error);
        if (first == fallback)
            return;

        Result second = await OpenAsync(fallback, progress, cancel).ConfigureAwait(false);
        if (!second.Ok)
            Log.Error($"The default domain does not open either: {second.Error}");
    }

    private static void RecordLoad(long start, string phase) =>
        Metrics.WorldLoadDurations.RecordSince(start, new KeyValuePair<string, object?>(Metrics.PhaseTag, phase));

    /// <summary>
    /// Records the time since <paramref name="start"/> as <paramref name="phase"/>, and starts the next phase.
    /// </summary>
    private static void MarkLoad(ref long start, string phase)
    {
        RecordLoad(start, phase);
        start = Metrics.WorldLoadDurations.Start();
    }

    /// <summary>
    /// The world as <paramref name="domains"/>' files make it, with its time running.
    /// </summary>
    private static WorldState GetBaseState(ImmutableArray<DomainRead> domains)
    {
        ImmutableArray<IComponent>[] entities = [.. domains.SelectMany(domain => GetEntities(domain))];
        KeyValuePair<string, IReadOnlyDictionary<string, IComponent>>[] placed = new KeyValuePair<
            string,
            IReadOnlyDictionary<string, IComponent>
        >[entities.Length];
        Parallel.For(
            0,
            entities.Length,
            i => placed[i] = new(GetKey(entities[i].OfType<Owner>().Single()), ToByType(entities[i]))
        );

        return new WorldState(
            new Dictionary<string, IReadOnlyDictionary<string, IComponent>>(placed),
            [],
            IsPaused: false,
            TimeScale: 1
        );
    }

    private static Dictionary<string, IComponent> ToByType(IEnumerable<IComponent> components) =>
        components.ToDictionary(component => component.GetType().Name);

    /// <summary>
    /// <paramref name="owner"/>'s entity's key in <see cref="WorldState.Placed"/>.
    /// </summary>
    private static string GetKey(Owner owner) => $"{owner.Domain.Id}/{owner.Prefab.Id}/{owner.Index}";

    /// <summary>
    /// The entities that the parts of a prefab spawn as: one for each part, as <see cref="Prefab.Place"/> orders and
    /// places them.
    /// </summary>
    private static ImmutableArray<ImmutableArray<IComponent>> GetEntities(Prefab prefab, Transform at)
    {
        ImmutableArray<PlacedPart> parts = prefab.Place(at);
        ImmutableArray<ImmutableArray<IComponent>>.Builder entities = ImmutableArray.CreateBuilder<
            ImmutableArray<IComponent>
        >(parts.Length);
        foreach ((PrefabPart part, Transform placed) in parts)
        {
            ImmutableArray<IComponent>.Builder components = ImmutableArray.CreateBuilder<IComponent>(
                part.Components.Length
            );
            foreach (IComponent component in part.Components)
            {
                components.Add(component is Transform ? placed : component);
            }

            entities.Add(components.MoveToImmutable());
        }

        return entities.MoveToImmutable();
    }

    /// <summary>
    /// Where <paramref name="domain"/> places each of its prefabs: its globals first, then its chunks.
    /// </summary>
    private static ImmutableArray<DomainPlace> GetPlaces(Domain domain)
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

    /// <summary>
    /// The entities <paramref name="domain"/>'s prefabs spawn as, each with its <see cref="Owner"/>.
    /// </summary>
    private static ImmutableArray<ImmutableArray<IComponent>> GetEntities(DomainRead domain)
    {
        ImmutableArray<ImmutableArray<IComponent>>.Builder entities = ImmutableArray.CreateBuilder<
            ImmutableArray<IComponent>
        >();
        Dictionary<Handle<Prefab>, int> spawned = [];
        foreach (PlacedPrefab placed in domain.Prefabs)
        {
            foreach (ImmutableArray<IComponent> part in GetEntities(placed.Prefab, placed.Place.At))
            {
                int before = spawned.GetValueOrDefault(placed.Place.Prefab);
                spawned[placed.Place.Prefab] = before + 1;
                entities.Add(part.Add(new Owner(domain.Domain, placed.Place.Prefab, before)));
            }
        }

        return entities.ToImmutable();
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the game thread once no other work on the world is running, and gives what it
    /// returns.
    /// </summary>
    private async Task<T> RunInTurnAsync<T>(Func<Task<T>> work, CancellationToken cancel)
    {
        await _turn.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            await _threads.Enter(EngineThread.Game);

            return await work().ConfigureAwait(false);
        }
        finally
        {
            HideLoading();
            _turn.Release();
        }
    }

    /// <summary>
    /// <see cref="RunInTurnAsync{T}"/> for work that returns nothing.
    /// </summary>
    private async Task RunInTurnAsync(Func<Task> work, CancellationToken cancel) =>
        await RunInTurnAsync(
                async () =>
                {
                    await work().ConfigureAwait(false);

                    return true;
                },
                cancel
            )
            .ConfigureAwait(false);

    /// <summary>
    /// One entity for each of <paramref name="entities"/>. Game thread.
    /// </summary>
    private ImmutableArray<Entity> CreateEntities(ImmutableArray<ImmutableArray<IComponent>> entities)
    {
        Entity[] created = new Entity[entities.Length];
        Ecs.CreateMany(entities.AsSpan(), created);

        return ImmutableCollectionsMarshal.AsImmutableArray(created);
    }

    /// <summary>
    /// <paramref name="entities"/> go, those not gone already. Game thread.
    /// </summary>
    private void DestroyEntities(ImmutableArray<Entity> entities) => Ecs.DestroyMany(entities.AsSpan());

    /// <summary>
    /// The world as it is: every entity's components, copied, and the pause and time scale. Game thread.
    /// </summary>
    private WorldState GetState()
    {
        Dictionary<string, IReadOnlyDictionary<string, IComponent>> placed = [];
        ImmutableArray<IReadOnlyDictionary<string, IComponent>>.Builder made = ImmutableArray.CreateBuilder<
            IReadOnlyDictionary<string, IComponent>
        >();
        foreach (EcsChunk chunk in Query())
        {
            foreach (Entity entity in chunk.Entities)
            {
                IReadOnlyDictionary<string, IComponent> components = ToByType(GetComponents(entity));
                if (Has<Owner>(entity))
                    placed[GetKey(Get<Owner>(entity))] = components;
                else
                    made.Add(components);
            }
        }

        return new WorldState(placed, made.ToImmutable(), IsPaused, TimeScale);
    }

    /// <summary>
    /// Frees what the saves kept of the script copies that no entity of the world holds any more. Game thread.
    /// </summary>
    /// <remarks>
    /// A save keeps the state of each copy that runs in the slot of its script, as a new set of values, so the sets of
    /// the save before it, and of every copy whose entity or script went, are held by nothing. While a save still
    /// writes, it reads the sets of the world it took, so this frees nothing and the next call frees them. Only the
    /// game thread starts a save, so none starts while this runs.
    /// </remarks>
    private void FreeKeptScriptState()
    {
        if (!_saving.IsCompleted)
            return;

        HashSet<ScriptValues> held = [];
        foreach (EcsChunk<ScriptContainer> chunk in Query<ScriptContainer>())
        {
            for (int i = 0; i < chunk.Entities.Length; i++)
            {
                foreach (ScriptSlot slot in chunk.First[i])
                {
                    held.Add(slot.Values);
                }
            }
        }

        ScriptValues.FreeUnheld(held);
    }

    /// <summary>
    /// Has each gem write what it holds of the world's entities into their components, so what reads the components
    /// next reads the state of now. Game thread.
    /// </summary>
    private void RunSaveHooks()
    {
        foreach (IHookIntoWorldSave hook in _saveHooks)
        {
            hook.OnWorldSaving();
        }
    }

    private string GetName(Handle<Domain> domain) => _assets.GetPath(domain) ?? "a domain";

    /// <summary>
    /// Reads the domain on workers, then puts it in place of the world with the player of the project.
    /// </summary>
    /// <remarks>
    /// A domain that does not load leaves the world as it was. The replace goes through the loading screen
    /// (<see cref="ReplaceAsync"/>).
    /// </remarks>
    private async Task<Result> OpenDomainAsync(
        Handle<Domain> domain,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        if (!HasEcs)
            return Result.Failure("no gem the project lists provides the ECS.");

        long openStart = Metrics.WorldLoadDurations.Start();
        ShowLoading($"Loading {GetName(domain)}...");
        ShowLoadingStep("Reading...");

        // The loading screen and the player are read at the same time as the domain.
        Task<ImmutableArray<ImmutableArray<IComponent>>> screen = ReadLoadingScreenAsync();
        Handle<Prefab> playerPrefab = _app.Project?.Player ?? Handle<Prefab>.None;
        Task<Result<Prefab>> playerRead = _assets.LoadAsync(playerPrefab, progress, cancel);
        Result<DomainRead> read = await ReadDomainAsync(domain, progress, cancel).ConfigureAwait(false);
        Result<Prefab> playerLoaded = await playerRead.ConfigureAwait(false);
        ImmutableArray<ImmutableArray<IComponent>> loadingScreen = await screen.ConfigureAwait(false);
        if (!read.Ok)
            return Result.Failure(read.Error);

        DomainRead opened = read.Value;
        ImmutableArray<ImmutableArray<IComponent>> placed = GetEntities(opened);
        ImmutableArray<ImmutableArray<IComponent>> player =
            playerPrefab == Handle<Prefab>.None ? [] : GetPlayer(domain, placed, playerLoaded);
        await ReplaceAsync(
                [.. placed, .. player],
                loadingScreen,
                isClearingGlobals: false,
                () =>
                {
                    ImmutableArray<Entity> coming = CreateEntities(placed);
                    _read[domain] = opened;
                    Domains = Domains.Add(domain);
                    Log.Info($"Opened {GetName(domain)}: {coming.Length} entities.");
                    CreateEntities(player);
                },
                cancel
            )
            .ConfigureAwait(false);
        RecordLoad(openStart, "open_domain");

        return Result.Success();
    }

    /// <summary>
    /// Puts <paramref name="arriving"/> in place of the world, and shows nothing half ready or left from the old world.
    /// </summary>
    /// <remarks>
    /// <paramref name="screen"/> holds the entities of the loading screen. The gems start to load what comes, and the world that shows fades into the cover. Behind the full cover, what is
    /// there leaves, the globals too when <paramref name="isClearingGlobals"/> is true, the loading screen comes in its
    /// place, the gems clean up after what left, and the cover fades into the loading screen. An empty world, or a headless run, skips these fades. The
    /// gems prepare what comes. When they are ready and no asset is in load, the loading screen fades into the cover,
    /// or goes to it at once when it showed without a fade. Behind the full cover, the loading screen leaves, the gems
    /// clean up after it, <paramref name="arrive"/> makes what comes, and the gems settle it. Then the cover fades into it. <see cref="IsLoading"/> is true while it runs.
    /// </remarks>
    private async Task ReplaceAsync(
        ImmutableArray<ImmutableArray<IComponent>> arriving,
        ImmutableArray<ImmutableArray<IComponent>> screen,
        bool isClearingGlobals,
        Action arrive,
        CancellationToken cancel
    )
    {
        IsLoading = true;
        long replaceStart = Metrics.WorldLoadDurations.Start();
        long phaseStart = replaceStart;
        try
        {
            // The gems start to load what comes now, so that it loads while the fades and the loading screen show.
            await _threads.Enter(EngineThread.Game);
            Task starting = RunHooksAsync(hooks => hooks.OnDomainReadAsync(arriving, cancel), "starting its loads");
            MarkLoad(ref phaseStart, "start_loads");

            // A world that shows fades into the cover, so that the loading screen takes its place hidden. An empty world
            // shows nothing, and a headless run shows nothing, so the loading screen takes its place at once.
            await _threads.Enter(EngineThread.Game);
            bool wasShowing = false;
            foreach (EcsChunk chunk in Query())
            {
                wasShowing |= !_app.IsHeadless && chunk.Entities.Length > 0;
            }

            if (wasShowing)
                await FadeCoverAsync(0, 1, cancel).ConfigureAwait(false);

            MarkLoad(ref phaseStart, "fade_out_world");

            // Everything in the world leaves but its globals, those too with isClearingGlobals. A global that stays
            // outlives the domain that owned it, so it is owned by none, as an entity made as the game runs.
            await _threads.Enter(EngineThread.Game);
            ImmutableArray<Entity>.Builder leaving = ImmutableArray.CreateBuilder<Entity>();
            ImmutableArray<Entity>.Builder staying = ImmutableArray.CreateBuilder<Entity>();
            foreach (EcsChunk chunk in Query())
            {
                foreach (Entity entity in chunk.Entities)
                {
                    bool isGlobal = Has<Tags>(entity) && Get<Tags>(entity).Has(Tag.Global);
                    if (isClearingGlobals || !isGlobal)
                        leaving.Add(entity);
                    else
                        staying.Add(entity);
                }
            }

            DestroyEntities(leaving.ToImmutable());
            foreach (Entity entity in staying)
            {
                Remove<Owner>(entity);
            }

            _read.Clear();
            Domains = [];

            // In the same turn as the destroy, so no frame shows an empty world while the gems clean up.
            ImmutableArray<Entity> loading = CreateEntities(screen);
            await RunHooksAsync(hooks => hooks.OnDomainUnloadedAsync(cancel), "cleaning up").ConfigureAwait(false);
            MarkLoad(ref phaseStart, "clear_world");

            // The cover fades into the loading screen once a frame of it is drawn. Its time stood still behind the full
            // cover and starts with this fade. Nothing waits for it to settle.
            await _threads.Enter(EngineThread.Game);
            if (wasShowing)
            {
                await WaitUntilDrawnAsync(cancel).ConfigureAwait(false);
                await FadeCoverAsync(1, 0, cancel).ConfigureAwait(false);
            }

            MarkLoad(ref phaseStart, "fade_to_loading_screen");
            await starting.ConfigureAwait(false);
            await _threads.Enter(EngineThread.Game);
            ShowLoadingStep($"Preparing {arriving.Length} entities...");
            await RunHooksAsync(hooks => hooks.OnDomainLoadingAsync(arriving, cancel), "preparing")
                .ConfigureAwait(false);
            MarkLoad(ref phaseStart, "on_domain_loading");

            // A gem that doesn't hook into domain loading may still be loading what comes.
            ShowLoadingStep("Waiting for the assets in load...");
            while (_assets.HasLoadsInFlight)
            {
                await WaitForFrameAsync(cancel).ConfigureAwait(false);
            }

            MarkLoad(ref phaseStart, "loads_in_flight_wait");
            if (wasShowing)
                await FadeCoverAsync(0, 1, cancel).ConfigureAwait(false);
            else
                CoverOpacity = 1;

            MarkLoad(ref phaseStart, "fade_out_loading_screen");

            await _threads.Enter(EngineThread.Game);
            DestroyEntities(loading);
            await RunHooksAsync(hooks => hooks.OnDomainUnloadedAsync(cancel), "cleaning up").ConfigureAwait(false);

            MarkLoad(ref phaseStart, "unload_loading_screen");
            await _threads.Enter(EngineThread.Game);
            arrive();
            MarkLoad(ref phaseStart, "arrive");

            // The world that came holds the sets of script state that its copies take up, so the sweep waits for it:
            // what the old world held is held by nothing now.
            FreeKeptScriptState();

            await WaitUntilDrawnAsync(cancel).ConfigureAwait(false);
            ShowLoadingStep("Settling...");
            MarkLoad(ref phaseStart, "wait_drawn");
            await SettleAsync(cancel).ConfigureAwait(false);
            MarkLoad(ref phaseStart, "settle");

            // The low-latency GC Program asks for seldom collects gen 2, so what the load, the unloads and the gems'
            // first copies of the arrived world threw away would stay committed: one collection that compacts and
            // decommits gives it back, behind the cover once that world's first frame is drawn.
            await _threads.Enter(EngineThread.Game);
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            MarkLoad(ref phaseStart, "gc");

            HideLoading();
            await FadeCoverAsync(1, 0, cancel).ConfigureAwait(false);
            MarkLoad(ref phaseStart, "fade_in");
            RecordLoad(replaceStart, "replace");
        }
        finally
        {
            IsLoading = false;
            CoverOpacity = 0;
        }
    }

    /// <summary>
    /// Waits until the hook of each gem settled what arrived, for at most <see cref="SettleLimitSec"/> seconds and
    /// <see cref="SettleLimitFrames"/> frames, whichever ends later.
    /// </summary>
    /// <remarks>
    /// At the limit, the hooks are cancelled and the world shows as it is. Game thread.
    /// </remarks>
    private async Task SettleAsync(CancellationToken cancel)
    {
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        Task settled = RunHooksAsync(hooks => hooks.OnDomainArrivedAsync(limit.Token), "settling");
        long start = Stopwatch.GetTimestamp();
        uint lastFrame = DrawnFrame + SettleLimitFrames;
        while (
            !settled.IsCompleted
            && (Stopwatch.GetElapsedTime(start).TotalSeconds < SettleLimitSec || DrawnFrame < lastFrame)
        )
        {
            await Task.WhenAny(settled, _frameStarting.Task).WaitAsync(cancel).ConfigureAwait(false);
            await _threads.Enter(EngineThread.Game);
        }

        await limit.CancelAsync().ConfigureAwait(false);
        try
        {
            await settled.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            Log.Warn(
                $"The gems did not settle in {SettleLimitSec} s and {SettleLimitFrames} frames, so the world shows as it is."
            );
        }
    }

    /// <summary>
    /// Changes <see cref="CoverOpacity"/> from <paramref name="from"/> to <paramref name="to"/> frame by frame, over
    /// <see cref="CoverFadeSec"/> seconds. Headless, nothing shows, so it changes at once.
    /// </summary>
    private async Task FadeCoverAsync(float from, float to, CancellationToken cancel)
    {
        long start = Stopwatch.GetTimestamp();
        float done = 0;
        while (!_app.IsHeadless && done < 1)
        {
            CoverOpacity = float.Lerp(from, to, done);
            await WaitForFrameAsync(cancel).ConfigureAwait(false);
            done = (float)(Stopwatch.GetElapsedTime(start).TotalSeconds / CoverFadeSec);
        }

        CoverOpacity = to;
    }

    /// <summary>
    /// Has the debug UI show what the load that runs loads, and under it what it does now, in this frame. Debug builds
    /// only, as the lines logged to show on screen.
    /// </summary>
    [Conditional("DEBUG")]
    private void DrawLoading()
    {
        if (_loadingHeader is not { } loading)
            return;

        _debugUI?.Text(loading);
        if (_loadingStep is { } step)
            _debugUI?.Text(step);
    }

    /// <summary>
    /// Logs <paramref name="loading"/>, what the load that starts loads, and has the debug UI show it each frame until
    /// the load ends.
    /// </summary>
    private void ShowLoading(string loading)
    {
        Log.Info(loading);
        _loadingStep = null;
        _loadingHeader = loading;
    }

    /// <summary>
    /// Logs <paramref name="step"/>, what the load does from now, and has the debug UI show it under what loads each
    /// frame until the next step or the end of the load.
    /// </summary>
    private void ShowLoadingStep(string step)
    {
        Log.Info(step);
        _loadingStep = $"    {step}";
    }

    private void HideLoading()
    {
        _loadingHeader = null;
        _loadingStep = null;
    }

    /// <summary>
    /// Runs <paramref name="hook"/> on each gem that hooks into domain loading, all at once, and waits for them.
    /// </summary>
    /// <remarks>
    /// A hook that fails is logged as failing at <paramref name="doing"/>, and the replace continues without it.
    /// </remarks>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever a gem's hook throws, it is logged and the replace goes on; the other gems' hooks still run."
    )]
    private async Task RunHooksAsync(Func<IHookIntoDomainLoading, Task> hook, string doing)
    {
        await Task.WhenAll(_hooks.Select(RunOneAsync)).ConfigureAwait(false);

        async Task RunOneAsync(IHookIntoDomainLoading gem)
        {
            try
            {
                await hook(gem).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error(
                    $"{GemRegistry.GetGemName(gem.GetType())} failed {doing} as the world changed, and it goes on without it: {ex}"
                );
            }
        }
    }

    /// <summary>
    /// Waits until the world as it is now shows: the next frame publishes it, wherever this goes on in the frame, and it
    /// shows once that frame is drawn. Goes on on the game thread.
    /// </summary>
    private async Task WaitUntilDrawnAsync(CancellationToken cancel)
    {
        uint published = PublishedFrame + 1;
        while (DrawnFrame < published)
        {
            await WaitForFrameAsync(cancel).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits for the next frame to start, and goes on there, on the game thread.
    /// </summary>
    private async Task WaitForFrameAsync(CancellationToken cancel)
    {
        await _frameStarting.Task.WaitAsync(cancel).ConfigureAwait(false);
        await _threads.Enter(EngineThread.Game);
    }

    /// <summary>
    /// Replaces everything, globals too, with <paramref name="saved"/>: its domains as their files make them, with its
    /// changes made on them.
    /// </summary>
    /// <remarks>
    /// Workers read the domains first, so a domain that does not open leaves the world as it was.
    /// </remarks>
    private async Task<Result> OpenSaveAsync(
        SaveData saved,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        if (!HasEcs)
            return Result.Failure("no gem the project lists provides the ECS.");

        ShowLoading($"Loading a save of {string.Join(", ", saved.Domains.Select(GetName))}...");
        ShowLoadingStep("Reading the domains...");
        Task<ImmutableArray<ImmutableArray<IComponent>>> screen = ReadLoadingScreenAsync();
        Result<DomainRead>[] reads = await Task.WhenAll(
                saved.Domains.Select(domain => ReadDomainAsync(domain, progress, cancel))
            )
            .ConfigureAwait(false);
        for (int i = 0; i < reads.Length; i++)
        {
            if (!reads[i].Ok)
                return Result.Failure($"{GetName(saved.Domains[i])} does not open: {reads[i].Error}");
        }

        ImmutableArray<DomainRead> domains = [.. reads.Select(read => read.Value)];
        ImmutableArray<ImmutableArray<IComponent>> entities = await Task.Run(
                () => saved.Changes.ApplyTo(GetBaseState(domains)),
                cancel
            )
            .ConfigureAwait(false);
        await ReplaceAsync(
                entities,
                await screen.ConfigureAwait(false),
                isClearingGlobals: true,
                () =>
                {
                    foreach (DomainRead domain in domains)
                    {
                        _read[domain.Domain] = domain;
                        Domains = Domains.Add(domain.Domain);
                    }

                    CreateEntities(entities);
                    IsPaused = saved.Changes.IsPaused;
                    TimeScale = saved.Changes.TimeScale;
                    Log.Info($"Put the world back: {entities.Length} entities in {domains.Length} domains.");
                },
                cancel
            )
            .ConfigureAwait(false);

        return Result.Success();
    }

    /// <summary>
    /// Makes again the entities of each open domain that is or places one of <paramref name="changed"/>, as its files
    /// now say.
    /// </summary>
    /// <remarks>
    /// What happened to the entities stays. A domain that no longer loads stays as it was, which is an error.
    /// </remarks>
    private Task ReopenAsync(HashSet<ulong> changed, CancellationToken cancel) =>
        RunInTurnAsync(
            async () =>
            {
                foreach (Handle<Domain> open in Domains)
                {
                    bool isChanged =
                        changed.Contains(open.Id)
                        || GetPlaces(_read[open].Asset).Any(place => changed.Contains(place.Prefab.Id));
                    if (isChanged)
                        await ReopenAsync(open, cancel).ConfigureAwait(false);
                }
            },
            cancel
        );

    /// <summary>
    /// Makes the entities of <paramref name="domain"/> again from its files, with the changes of the game on top.
    /// </summary>
    /// <remarks>
    /// The changes are against the files as they were. A value that the game changed keeps that change, a value that it
    /// did not change takes the value of the file, and a destroyed entity stays destroyed.
    /// </remarks>
    private async Task ReopenAsync(Handle<Domain> domain, CancellationToken cancel)
    {
        Result<DomainRead> read = await ReadDomainAsync(domain, null, cancel).ConfigureAwait(false);
        if (!read.Ok)
        {
            await _threads.Enter(EngineThread.Game);
            Log.Error($"{GetName(domain)} is not opened again: {read.Error}");

            return;
        }

        // What the files make, on a worker. Nothing of the game's entities is read here, as frames run while it works.
        DomainRead was = _read[domain];
        (WorldState wasState, WorldState fileState) = await Task.Run(
                () => (GetBaseState([was]), GetBaseState([read.Value])),
                cancel
            )
            .ConfigureAwait(false);

        // From here to the end there is no wait, so no frame runs between what is read of the entities and the change
        // of them: a change of the game is in the copy, and an entity that the game destroyed stays destroyed.
        await _threads.Enter(EngineThread.Game);

        // What the gems hold of the entities goes into their components first, so the entities that come again take up
        // the state of now and not that of the last save.
        RunSaveHooks();

        // The domain's entities as they are, by their keys.
        ImmutableArray<Entity>.Builder leaving = ImmutableArray.CreateBuilder<Entity>();
        Dictionary<string, IReadOnlyDictionary<string, IComponent>> now = [];
        foreach (EcsChunk<Owner> chunk in Query<Owner>())
        {
            for (int i = 0; i < chunk.Entities.Length; i++)
            {
                if (chunk.First[i].Domain != domain)
                    continue;

                leaving.Add(chunk.Entities[i]);
                now[GetKey(chunk.First[i])] = ToByType(GetComponents(chunk.Entities[i]));
            }
        }

        WorldState live = new(now, [], IsPaused: false, TimeScale: 1);
        ImmutableArray<ImmutableArray<IComponent>> kept = WorldChanges.Between(wasState, live).ApplyTo(fileState);
        DestroyEntities(leaving.ToImmutable());
        ImmutableArray<Entity> made = CreateEntities(kept);
        _read[domain] = read.Value;
        Log.Info($"Opened {GetName(domain)} again: {made.Length} entities, with what changed in them kept.");
    }

    /// <summary>
    /// Returns the entities of the loading screen of the project, else of that of the engine, a black void. Game thread.
    /// </summary>
    /// <remarks>
    /// When the loading screen does not load, there are none, with a warning.
    /// </remarks>
    private async Task<ImmutableArray<ImmutableArray<IComponent>>> ReadLoadingScreenAsync()
    {
        Handle<Domain> project = _app.Project?.LoadingScreen ?? Handle<Domain>.None;
        Handle<Domain> screen =
            project == Handle<Domain>.None ? _assets.Find<Domain>("Domains/Loading/Loading.domain") : project;
        if (screen == Handle<Domain>.None)
            return [];

        Result<DomainRead> read = await ReadDomainAsync(screen, null, _app.Lifetime).ConfigureAwait(false);
        if (read.Ok)
            return GetEntities(read.Value);

        Log.Warn($"No loading screen is shown: {read.Error}");

        return [];
    }

    /// <summary>
    /// Loads <paramref name="handle"/> and the prefabs that it places, with where it places them.
    /// </summary>
    /// <remarks>
    /// A prefab that does not load is an error, and is left out. It returns why not when the domain does not load or no
    /// gem reads prefabs.
    /// </remarks>
    private async Task<Result<DomainRead>> ReadDomainAsync(
        Handle<Domain> handle,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        long start = Metrics.WorldLoadDurations.Start();
        Result<DomainRead> read = await ReadDomainUntimedAsync(handle, progress, cancel).ConfigureAwait(false);
        RecordLoad(start, "domain_read");

        return read;
    }

    private async Task<Result<DomainRead>> ReadDomainUntimedAsync(
        Handle<Domain> handle,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        Result<Domain> domain = await _assets.LoadAsync(handle, progress, cancel).ConfigureAwait(false);
        if (!domain.Ok)
            return Result.Failure(domain.Error);

        ImmutableArray<DomainPlace> places = GetPlaces(domain.Value);
        if (places.Length > 0 && !_assets.CanImport<Prefab>())
            return Result.Failure($"{GetName(handle)} holds prefabs, and no gem the project lists reads them.");

        // The prefabs of the places, loaded at once, telling how far that is, at the same indexes.
        const string Work = "Reading prefabs";
        int done = 0;
        progress?.Report(new LoadProgress(Work, 0, places.Length));
        Task<Result<Prefab>>[] loading = new Task<Result<Prefab>>[places.Length];
        for (int i = 0; i < loading.Length; i++)
        {
            loading[i] = LoadOneAsync(places[i].Prefab);
        }

        Result<Prefab>[] prefabs = await Task.WhenAll(loading).ConfigureAwait(false);
        ImmutableArray<PlacedPrefab>.Builder read = ImmutableArray.CreateBuilder<PlacedPrefab>(places.Length);
        for (int i = 0; i < places.Length; i++)
        {
            if (prefabs[i] is { Ok: true, Value: { } prefab })
                read.Add(new PlacedPrefab(places[i], prefab));
            else
                Log.Error($"{GetName(handle)} goes without a prefab: {prefabs[i].Error}");
        }

        return Result.Success(new DomainRead(handle, domain.Value, read.ToImmutable()));

        async Task<Result<Prefab>> LoadOneAsync(Handle<Prefab> prefab)
        {
            Result<Prefab> one = await _assets.LoadAsync(prefab, null, cancel).ConfigureAwait(false);
            progress?.Report(new LoadProgress(Work, Interlocked.Increment(ref done), places.Length));

            return one;
        }
    }

    /// <summary>
    /// Returns the entities of the player of the project, <paramref name="loaded"/>, at the first of
    /// <paramref name="placed"/> tagged spawn.
    /// </summary>
    /// <remarks>
    /// <paramref name="domain"/> does not own them, so they stay when it opens again, and go when another domain
    /// replaces it. There are none when the player did not load, which is an error.
    /// </remarks>
    private ImmutableArray<ImmutableArray<IComponent>> GetPlayer(
        Handle<Domain> domain,
        ImmutableArray<ImmutableArray<IComponent>> placed,
        Result<Prefab> loaded
    )
    {
        if (!loaded.Ok)
        {
            Log.Error($"The project's player is not spawned: {loaded.Error}");

            return [];
        }

        // Where the player goes: the first of the placed tagged spawn, so the globals of a domain before its chunks;
        // else the origin, with a warning.
        Transform? spawn = null;
        foreach (ImmutableArray<IComponent> entity in placed)
        {
            bool isSpawn = false;
            Transform? place = null;
            foreach (IComponent component in entity)
            {
                if (component is Tags tags)
                    isSpawn = tags.Has(Tag.Spawn);
                else if (component is Transform transform)
                    place = transform;
            }

            if (isSpawn && place is { } at)
            {
                spawn = new Transform { Position = at.Position, Rotation = at.Rotation };
                break;
            }
        }

        if (spawn is null)
            Log.Warn($"{GetName(domain)} has no entity tagged spawn, so the player is put at its origin.");

        return GetEntities(loaded.Value, spawn ?? new Transform());
    }
}
