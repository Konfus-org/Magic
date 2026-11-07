using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Text.Json;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Events;
using Magic.Files;
using Magic.Files.Types;
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
/// An entity a domain places, by its key, with its components as the domain's files make it.
/// </summary>
internal readonly record struct Placement(EntityKey Key, ImmutableArray<IComponent> Components);

/// <summary>
/// The game world: what is in it, the one place to load content into it, and the way to its entities. Every entity call
/// goes to the ECS a gem provides. What comes and goes
/// does so one at a time, and each entity a domain placed is kept by its key, for saves and for when the domain goes. As the run starts it puts
/// back the world the last run kept, or opens the entry point, without waiting for it, as frames run while it loads; as the
/// run ends for a restart it keeps the world for the next; and it changes for what the assets say changed. Entity calls are for the main thread; the others, any.
/// </summary>
public sealed class World : IEcs, IDisposable
{
    private readonly Assets _assets;

    private readonly IFileSystem _files;

    private readonly EventCoordinator _events;

    private readonly Threads _threads;

    private readonly App _app;

    /// <summary>
    /// Held while work on the world runs, so it runs in turn.
    /// </summary>
    private readonly SemaphoreSlim _turn = new(1, 1);

    /// <summary>
    /// Each entity the open domains placed, by its key. A destroyed one keeps its key, with a handle to nothing. Main thread.
    /// </summary>
    private readonly Dictionary<EntityKey, Entity> _placed = [];

    /// <summary>
    /// The prefabs each open domain placed, as they were read: what its entities are compared with, by a save and as the
    /// domain opens again. Main thread.
    /// </summary>
    private readonly Dictionary<Handle<Domain>, ImmutableArray<PlacedPrefab>> _read = [];

    /// <summary>
    /// The ECS a gem provides, from when it is made until it goes; null while none does.
    /// </summary>
    private IEcs? _ecs;

    /// <summary>
    /// Completed as the next frame starts, and replaced for the one after, by <see cref="Pump"/>: what a replace waits on
    /// frame by frame. Main thread.
    /// </summary>
    private TaskCompletionSource _frameStarting = new();

    /// <summary>
    /// The gems that hook into domain loading, in the order they were made. Main thread.
    /// </summary>
    private ImmutableArray<IHookIntoDomainLoading> _hooks = [];

    internal World(Assets assets, IFileSystem files, EventCoordinator events, Threads threads, App app)
    {
        _assets = assets;
        _files = files;
        _events = events;
        _threads = threads;
        _app = app;
    }

    /// <summary>
    /// The domains open, in the order they came, without the loading screen.
    /// </summary>
    public ImmutableArray<Handle<Domain>> Domains { get; private set; } = [];

    /// <summary>
    /// Whether a domain or a saved world is replacing what is in the world: from when the loading screen shows until a
    /// frame has drawn what replaced it.
    /// </summary>
    public bool IsLoading { get; private set; }

    /// <summary>
    /// Whether the world's time stands still, from the next frame, with no fixed steps. Ignored while it loads, as the
    /// gems may need steps to get ready.
    /// </summary>
    public bool IsPaused { get; set; }

    /// <summary>
    /// How fast the world's time runs against real time, from the next frame; below 0 is 0. Ignored while it loads.
    /// </summary>
    public float TimeScale
    {
        get;
        set => field = Math.Max(value, 0);
    } = 1;

    /// <summary>
    /// Whether the loading screen is what the world holds, until what replaces it comes: while it is, the world's own pause
    /// and time scale wait for it.
    /// </summary>
    internal bool IsShowingLoadingScreen { get; private set; }

    /// <summary>
    /// The last frame whose hooks before Render have run, so what it made of the world is published for the render thread.
    /// Set on the main thread, read on any.
    /// </summary>
    internal uint PublishedFrame
    {
        get => Volatile.Read(ref field);
        set => Volatile.Write(ref field, value);
    }

    /// <summary>
    /// The newest published frame the render thread has drawn, or one after it. Set on the render thread, read on any.
    /// </summary>
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

    public void Dispose() => _turn.Dispose();

    /// <summary>
    /// Opens <paramref name="domain"/> in place of what is in the world, all but its globals, with the loading screen shown
    /// until it is there and the project's player at its spawn. It is read first on workers, so one that doesn't load leaves
    /// the world as it was. Why not, when it doesn't load or no gem provides the ECS.
    /// </summary>
    public async Task<Result> OpenAsync(
        Handle<Domain> domain,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        Result opened = await RunLoadingAsync(() => OpenDomainAsync(domain, progress, cancel), cancel)
            .ConfigureAwait(false);

        return opened.Ok ? opened : Result.Failure($"The domain does not open: {opened.Error}");
    }

    /// <summary>
    /// Puts <paramref name="prefab"/>, loaded first, in the world at <paramref name="at"/>: the entities it made, which are
    /// the caller's to destroy, and go when a domain replaces the world if they are still there. Why not, when it doesn't load
    /// or no gem provides the ECS. Single entities need none of this: make them with <see cref="Create"/>.
    /// </summary>
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
    /// A new entity with <paramref name="components"/>, made now. One whose <see cref="Tags"/> hold <see cref="Tag.Global"/>
    /// outlives every domain that replaces the world.
    /// </summary>
    public Entity Create(ReadOnlySpan<IComponent> components) => Ecs.Create(components);

    /// <summary>
    /// Saves the game as <c>Saves/<paramref name="name"/>.save</c> beside Magic: the domains open, what changed against
    /// their files, and the pause and time scale. Why not, when JSON can't hold it or it can't be written.
    /// </summary>
    public async Task<Result> SaveAsync(string name, CancellationToken cancel)
    {
        SaveFile saving = await RunInTurnAsync(() => Task.FromResult(GetSaveFile()), cancel).ConfigureAwait(false);
        Result<byte[]> bytes = await Task.Run(() => ToBytes(saving), cancel).ConfigureAwait(false);
        if (!bytes.Ok)
            return Result.Failure(bytes.Error);

        return await _files.WriteAsync(GetSavePath(name), bytes.Value, cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Puts the game back as <c>Saves/<paramref name="name"/>.save</c> holds it, in place of everything in the world,
    /// globals too: its domains opened from their files, then its changes made. Why not, when there is no such save, it
    /// doesn't read, or one of its domains doesn't open, which leaves the world as it was.
    /// </summary>
    public async Task<Result> RestoreAsync(string name, CancellationToken cancel)
    {
        Result<byte[]> bytes = await _files.ReadBytesAsync(GetSavePath(name), cancel).ConfigureAwait(false);
        if (!bytes.Ok)
            return Result.Failure(bytes.Error);

        return await RestoreAsync(bytes.Value, cancel).ConfigureAwait(false);
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

    /// <summary>
    /// Changes the world for the prefabs and domains the assets read again, as the last frame's take heard it, and lets
    /// a replace waiting for this frame go on. Main thread, as each frame starts, before the gems' hooks.
    /// </summary>
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

        // Let a replace waiting for this frame go on.
        TaskCompletionSource starting = _frameStarting;
        _frameStarting = new TaskCompletionSource();
        starting.SetResult();
    }

    /// <summary>
    /// Has <paramref name="ecs"/> hold the world's entities, until the subscription is disposed. Main thread.
    /// </summary>
    internal Subscription Register(IEcs ecs)
    {
        _ecs = ecs;

        return new Subscription(() => _ecs = null);
    }

    /// <summary>
    /// Has <paramref name="hooks"/> take part as domains replace the world, until the subscription is disposed. Main thread.
    /// </summary>
    internal Subscription Register(IHookIntoDomainLoading hooks)
    {
        _hooks = _hooks.Add(hooks);

        return new Subscription(() => _hooks = _hooks.Remove(hooks));
    }

    /// <summary>
    /// Every entity with a <see cref="Spin"/> turned on by <paramref name="deltaSec"/> of the world's time. Main thread.
    /// </summary>
    internal void TurnSpins(float deltaSec)
    {
        if (deltaSec <= 0)
            return;

        foreach (EcsMutableChunk<Transform, Spin> chunk in Mutate<Transform, Spin>())
        {
            for (int i = 0; i < chunk.Entities.Length; i++)
            {
                chunk.First[i].Rotation = chunk.Second[i].Turn(chunk.First[i].Rotation, deltaSec);
            }
        }
    }

    /// <summary>
    /// Puts back the JSON <paramref name="kept"/>; the entry point opens when there is none or it doesn't read, which is an
    /// error. That is the project's, or the default one when it names none or that doesn't open. With no ECS, nothing
    /// opens, which is warned about.
    /// </summary>
    internal async Task StartAsync(byte[]? kept, CancellationToken cancel)
    {
        if (kept is not null)
        {
            Result restored = await RestoreAsync(kept, cancel).ConfigureAwait(false);
            if (restored.Ok)
                return;

            Log.Error($"The world goes back to the entry point: the kept world does not open: {restored.Error}");
        }

        if (!HasEcs)
        {
            Log.Warn("No gem the project lists provides the ECS, so the world holds no entities, and no domain opens.");

            return;
        }

        Handle<Domain> fallback = _assets.GetDefault<Domain>();
        Handle<Domain> entry = _app.Project?.EntryPoint ?? Handle<Domain>.None;
        Handle<Domain> first = entry == Handle<Domain>.None ? fallback : entry;
        Result opened = await OpenAsync(first, null, cancel).ConfigureAwait(false);
        if (opened.Ok)
            return;

        Log.Error(opened.Error);
        if (first == fallback)
            return;

        Result second = await OpenAsync(fallback, null, cancel).ConfigureAwait(false);
        if (!second.Ok)
            Log.Error($"The default domain does not open either: {second.Error}");
    }

    /// <summary>
    /// The world as JSON for the next run, kept as a save is: what changed against the open domains' prefabs as they were
    /// read; none while it is still arriving, so the next run opens the entry point, or when JSON can't hold it. Main
    /// thread.
    /// </summary>
    internal byte[]? Keep()
    {
        if (IsLoading || Domains.IsEmpty)
            return null;

        SaveFile saved = GetSaveFile();
        Result<byte[]> bytes = ToBytes(saved);
        if (!bytes.Ok)
        {
            Log.Error($"The world goes back to the entry point: it could not be saved: {bytes.Error}");

            return null;
        }

        Log.Info($"Kept the world: {saved.Changes.Entities.Length} changes in {saved.Domains.Length} domains.");

        return bytes.Value;
    }

    /// <summary>
    /// <paramref name="saved"/> as UTF-8 JSON; why not, when JSON can't hold it.
    /// </summary>
    private static Result<byte[]> ToBytes(SaveFile saved)
    {
        Result<JsonElement> json = Json.Write(saved, typeof(SaveFile));

        return json.Ok ? Result.Success(Encoding.UTF8.GetBytes(json.Value.GetRawText())) : Result.Failure(json.Error);
    }

    /// <summary>
    /// A prefab's parts as the entities it spawns as: one for each part, a part before the parts under it, its transform
    /// placed under its parent's, and the top parts' under where it is put.
    /// </summary>
    private static ImmutableArray<ImmutableArray<IComponent>> GetEntities(Prefab prefab, Transform at)
    {
        ImmutableArray<ImmutableArray<IComponent>>.Builder entities = ImmutableArray.CreateBuilder<
            ImmutableArray<IComponent>
        >();
        AddEntities(prefab.Parts, at, entities);

        return entities.ToImmutable();
    }

    private static void AddEntities(
        ImmutableArray<PrefabPart> parts,
        Transform parent,
        ImmutableArray<ImmutableArray<IComponent>>.Builder entities
    )
    {
        foreach (PrefabPart part in parts)
        {
            // The part's transform placed under its parent's, or the parent's place when it has none.
            Transform placed = part
                .Components.OfType<Transform>()
                .Select(local => local.Under(parent))
                .FirstOrDefault(parent);
            ImmutableArray<IComponent>.Builder components = ImmutableArray.CreateBuilder<IComponent>(
                part.Components.Length
            );
            foreach (IComponent component in part.Components)
            {
                components.Add(component is Transform ? placed : component);
            }

            entities.Add(components.MoveToImmutable());
            AddEntities(part.Children, placed, entities);
        }
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

    private static ImmutableArray<ImmutableArray<IComponent>> GetComponents(ImmutableArray<Placement> placements) =>
        [.. placements.Select(placement => placement.Components)];

    /// <summary>
    /// The entities <paramref name="prefabs"/> spawn as in <paramref name="domain"/>, each by its key.
    /// </summary>
    private static ImmutableArray<Placement> GetPlacements(Handle<Domain> domain, ImmutableArray<PlacedPrefab> prefabs)
    {
        ImmutableArray<Placement>.Builder placements = ImmutableArray.CreateBuilder<Placement>();
        foreach (PlacedPrefab placed in prefabs)
        {
            ImmutableArray<ImmutableArray<IComponent>> parts = GetEntities(placed.Prefab, placed.Place.At);
            for (int part = 0; part < parts.Length; part++)
            {
                placements.Add(new Placement(new EntityKey(domain, placed.Place.Prefab, part), parts[part]));
            }
        }

        return placements.ToImmutable();
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the main thread once no other work on the world is running, and gives what it
    /// returns.
    /// </summary>
    private async Task<T> RunInTurnAsync<T>(Func<Task<T>> work, CancellationToken cancel)
    {
        await _turn.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            await _threads.Enter(EngineThread.Main);

            return await work().ConfigureAwait(false);
        }
        finally
        {
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
    /// <paramref name="domain"/> comes, and is in the world from then on. Main thread.
    /// </summary>
    private void Add(Handle<Domain> domain, ImmutableArray<PlacedPrefab> prefabs)
    {
        ImmutableArray<Placement> coming = GetPlacements(domain, prefabs);
        Place(coming);
        _read[domain] = prefabs;
        Domains = Domains.Add(domain);
        Log.Info($"Opened {GetName(domain)}: {coming.Length} entities.");
    }

    /// <summary>
    /// One entity for each of <paramref name="entities"/>. Main thread.
    /// </summary>
    private ImmutableArray<Entity> CreateEntities(ImmutableArray<ImmutableArray<IComponent>> entities)
    {
        ImmutableArray<Entity>.Builder created = ImmutableArray.CreateBuilder<Entity>(entities.Length);
        foreach (ImmutableArray<IComponent> components in entities)
        {
            created.Add(Create(components.AsSpan()));
        }

        return created.MoveToImmutable();
    }

    /// <summary>
    /// One entity for each of <paramref name="placements"/>, kept by its key. Main thread.
    /// </summary>
    private void Place(ImmutableArray<Placement> placements)
    {
        foreach (Placement placement in placements)
        {
            _placed[placement.Key] = Create(placement.Components.AsSpan());
        }
    }

    /// <summary>
    /// The entities <paramref name="domain"/> placed go, and their keys with them. Main thread.
    /// </summary>
    private void Unplace(Handle<Domain> domain)
    {
        foreach (EntityKey key in _placed.Keys.Where(key => key.Domain == domain).ToArray())
        {
            if (Ecs.IsAlive(_placed[key]))
                Destroy(_placed[key]);

            _placed.Remove(key);
        }
    }

    /// <summary>
    /// <paramref name="entities"/> go, those not gone already. Main thread.
    /// </summary>
    private void DestroyEntities(ImmutableArray<Entity> entities)
    {
        foreach (Entity entity in entities)
        {
            if (Ecs.IsAlive(entity))
                Destroy(entity);
        }
    }

    /// <summary>
    /// Runs <paramref name="change"/> as a change, which may set <see cref="IsLoading"/>, and ends with it unset.
    /// </summary>
    private Task<Result> RunLoadingAsync(Func<Task<Result>> change, CancellationToken cancel) =>
        RunInTurnAsync(
            async () =>
            {
                try
                {
                    return await change().ConfigureAwait(false);
                }
                finally
                {
                    IsLoading = false;
                    IsShowingLoadingScreen = false;
                }
            },
            cancel
        );

    /// <summary>
    /// Puts the world back as the JSON <paramref name="bytes"/> hold it, in place of everything in the world. Why not,
    /// when it doesn't read or one of its domains doesn't open, which leaves the world as it was.
    /// </summary>
    private async Task<Result> RestoreAsync(byte[] bytes, CancellationToken cancel)
    {
        Result<SaveFile> saved = Json.Read<SaveFile>(bytes);
        if (!saved.Ok)
            return Result.Failure(saved.Error);

        return await RunLoadingAsync(() => OpenSaveAsync(saved.Value, cancel), cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// The world as a save holds it: the domains open, what changed against their files, and the pause and time scale.
    /// Main thread.
    /// </summary>
    private SaveFile GetSaveFile() => new(Domains, FindChanges(Domains), IsPaused, TimeScale);

    private string GetName(Handle<Domain> domain) => _assets.GetPath(domain) ?? "a domain";

    /// <summary>
    /// Reads the domain on workers, so one that doesn't load leaves the world as it was, then puts it in place of the world
    /// with the project's player, through the loading screen (<see cref="ReplaceAsync"/>).
    /// </summary>
    private async Task<Result> OpenDomainAsync(
        Handle<Domain> domain,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        if (!HasEcs)
            return Result.Failure("no gem the project lists provides the ECS.");

        Result<ImmutableArray<PlacedPrefab>> read = await ReadDomainAsync(domain, progress, cancel)
            .ConfigureAwait(false);
        if (!read.Ok)
            return Result.Failure(read.Error);

        ImmutableArray<ImmutableArray<IComponent>> placed = GetComponents(GetPlacements(domain, read.Value));
        ImmutableArray<ImmutableArray<IComponent>> player = await ReadPlayerAsync(domain, placed, progress, cancel)
            .ConfigureAwait(false);
        await ReplaceAsync(
                [.. placed, .. player],
                isClearingGlobals: false,
                () =>
                {
                    Add(domain, read.Value);
                    CreateEntities(player);
                },
                cancel
            )
            .ConfigureAwait(false);

        return Result.Success();
    }

    /// <summary>
    /// Puts <paramref name="arriving"/> in place of the world with nothing of it shown half ready, nor anything of what was
    /// there before: what is there leaves, globals too with <paramref name="isClearingGlobals"/>, the gems clean up after it,
    /// and the loading screen shows while they prepare what comes. Once they have and no asset is on its way, the loading
    /// screen leaves, the gems clean up after it, <paramref name="arrive"/> makes what comes, and the replace ends after a
    /// frame has drawn it. Sets <see cref="IsLoading"/>, for the caller to unset.
    /// </summary>
    private async Task ReplaceAsync(
        ImmutableArray<ImmutableArray<IComponent>> arriving,
        bool isClearingGlobals,
        Action arrive,
        CancellationToken cancel
    )
    {
        IsLoading = true;

        // The project's loading screen, else the engine's, a black void: the entities it is; none, which is warned
        // about, when it doesn't load.
        Handle<Domain> project = _app.Project?.LoadingScreen ?? Handle<Domain>.None;
        Handle<Domain> screenDomain =
            project == Handle<Domain>.None ? _assets.Find<Domain>("Domains/Loading/Loading.domain") : project;
        ImmutableArray<ImmutableArray<IComponent>> screen = [];
        if (screenDomain != Handle<Domain>.None)
        {
            Result<ImmutableArray<PlacedPrefab>> read = await ReadDomainAsync(screenDomain, null, cancel)
                .ConfigureAwait(false);
            if (read.Ok)
                screen = GetComponents(GetPlacements(screenDomain, read.Value));
            else
                Log.Warn($"No loading screen is shown: {read.Error}");
        }

        // Everything in the world leaves but its globals, those too with isClearingGlobals.
        await _threads.Enter(EngineThread.Main);
        ImmutableArray<Entity>.Builder leaving = ImmutableArray.CreateBuilder<Entity>();
        foreach (EcsChunk chunk in Query())
        {
            foreach (Entity entity in chunk.Entities)
            {
                bool isGlobal = Has<Tags>(entity) && Get<Tags>(entity).Has(Tag.Global);
                if (isClearingGlobals || !isGlobal)
                    leaving.Add(entity);
            }
        }

        DestroyEntities(leaving.ToImmutable());
        _placed.Clear();
        _read.Clear();
        Domains = [];
        await RunHooksAsync(hooks => hooks.OnDomainUnloadedAsync(cancel), "cleaning up").ConfigureAwait(false);

        await _threads.Enter(EngineThread.Main);
        ImmutableArray<Entity> loading = CreateEntities(screen);
        IsShowingLoadingScreen = true;
        await RunHooksAsync(hooks => hooks.OnDomainLoadingAsync(arriving, cancel), "preparing").ConfigureAwait(false);

        // A gem that doesn't hook into domain loading may still be loading what comes.
        while (_assets.HasLoadsInFlight)
        {
            await WaitForFrameAsync(cancel).ConfigureAwait(false);
        }

        await _threads.Enter(EngineThread.Main);
        DestroyEntities(loading);
        await RunHooksAsync(hooks => hooks.OnDomainUnloadedAsync(cancel), "cleaning up").ConfigureAwait(false);

        await _threads.Enter(EngineThread.Main);
        arrive();
        IsShowingLoadingScreen = false;

        // The next frame publishes what came, wherever this went on in the frame; it has shown once that one is drawn.
        uint arrived = PublishedFrame + 1;
        while (DrawnFrame < arrived)
        {
            await WaitForFrameAsync(cancel).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs <paramref name="hook"/> on every gem that hooks into domain loading, all at once, and waits for them. One that
    /// fails is logged as failing at <paramref name="doing"/>, and the replace goes on without it.
    /// </summary>
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
                Log.Error($"{gem.GetType().Name} failed {doing} as the world changed, and it goes on without it: {ex}");
            }
        }
    }

    /// <summary>
    /// Waits for the next frame to start, and goes on there, on the main thread.
    /// </summary>
    private async Task WaitForFrameAsync(CancellationToken cancel)
    {
        await _frameStarting.Task.WaitAsync(cancel).ConfigureAwait(false);
        await _threads.Enter(EngineThread.Main);
    }

    /// <summary>
    /// Everything leaves, globals too, and <paramref name="saved"/> comes: each of its domains opened from its files, read
    /// first on workers, so one that doesn't open leaves the world as it was, then its changes made.
    /// </summary>
    private async Task<Result> OpenSaveAsync(SaveFile saved, CancellationToken cancel)
    {
        if (!HasEcs)
            return Result.Failure("no gem the project lists provides the ECS.");

        List<(Handle<Domain> Domain, ImmutableArray<PlacedPrefab> Prefabs)> domains = [];
        foreach (Handle<Domain> domain in saved.Domains)
        {
            Result<ImmutableArray<PlacedPrefab>> read = await ReadDomainAsync(domain, null, cancel)
                .ConfigureAwait(false);
            if (!read.Ok)
                return Result.Failure($"{GetName(domain)} does not open: {read.Error}");

            domains.Add((domain, read.Value));
        }

        await ReplaceAsync(
                [
                    .. domains.SelectMany(domain => GetComponents(GetPlacements(domain.Domain, domain.Prefabs))),
                    .. saved
                        .Changes.Entities.Where(change => change.Kind != EntityChangeKind.Destroy)
                        .Select(change => change.After),
                ],
                isClearingGlobals: true,
                () =>
                {
                    foreach ((Handle<Domain> domain, ImmutableArray<PlacedPrefab> prefabs) in domains)
                    {
                        Add(domain, prefabs);
                    }

                    saved.Changes.Apply(this, _placed);
                    IsPaused = saved.IsPaused;
                    TimeScale = saved.TimeScale;
                    Log.Info(
                        $"Put the world back: {saved.Changes.Entities.Length} changes in {domains.Count} domains."
                    );
                },
                cancel
            )
            .ConfigureAwait(false);

        return Result.Success();
    }

    /// <summary>
    /// What changed in <paramref name="domains"/> against their prefabs as they were read, and every entity no domain placed.
    /// Main thread.
    /// </summary>
    private WorldChanges FindChanges(ImmutableArray<Handle<Domain>> domains)
    {
        Dictionary<EntityKey, ImmutableArray<IComponent>> authored = [];
        foreach (Handle<Domain> domain in domains)
        {
            foreach (Placement placement in GetPlacements(domain, _read.GetValueOrDefault(domain, [])))
            {
                authored[placement.Key] = placement.Components;
            }
        }

        return new WorldChanges(Ecs, _placed, authored);
    }

    private string GetSavePath(string name) => _files.Combine(AppContext.BaseDirectory, "Saves", name + ".save");

    /// <summary>
    /// Each open domain that is or places one of <paramref name="changed"/> makes its entities again as its files now say,
    /// keeping what happened to them; one that no longer loads stays as it was, which is an error.
    /// </summary>
    private Task ReopenAsync(HashSet<ulong> changed, CancellationToken cancel) =>
        RunInTurnAsync(
            async () =>
            {
                foreach (Handle<Domain> open in Domains)
                {
                    Result<Domain> domain = await _assets.LoadAsync(open, null, cancel).ConfigureAwait(false);
                    if (!domain.Ok)
                    {
                        Log.Error($"{GetName(open)} is not opened again: {domain.Error}");

                        continue;
                    }

                    bool isChanged =
                        changed.Contains(open.Id)
                        || GetPlaces(domain.Value).Any(place => changed.Contains(place.Prefab.Id));
                    if (isChanged)
                        await ReopenAsync(open, cancel).ConfigureAwait(false);
                }
            },
            cancel
        );

    /// <summary>
    /// <paramref name="domain"/>'s entities made again from its files as they are now, then what changed in them against its
    /// files as they were made again on top: a component changed as the game ran keeps its value, one that didn't takes the
    /// file's, and a destroyed entity stays destroyed.
    /// </summary>
    private async Task ReopenAsync(Handle<Domain> domain, CancellationToken cancel)
    {
        Result<ImmutableArray<PlacedPrefab>> read = await ReadDomainAsync(domain, null, cancel).ConfigureAwait(false);
        await _threads.Enter(EngineThread.Main);
        if (!read.Ok)
        {
            Log.Error($"{GetName(domain)} is not opened again: {read.Error}");

            return;
        }

        WorldChanges kept = new([.. FindChanges([domain]).Entities.Where(change => change.Key.Domain == domain)]);
        Unplace(domain);
        ImmutableArray<Placement> placements = GetPlacements(domain, read.Value);
        Place(placements);
        _read[domain] = read.Value;
        kept.Apply(this, _placed);
        Log.Info(
            $"Opened {GetName(domain)} again: {placements.Length} entities, {kept.Entities.Length} changed ones kept."
        );
    }

    /// <summary>
    /// The prefabs <paramref name="handle"/> places, loaded, and where. A prefab that doesn't load is an error, and it is left
    /// out; why not, when the domain doesn't load or no gem reads prefabs.
    /// </summary>
    private async Task<Result<ImmutableArray<PlacedPrefab>>> ReadDomainAsync(
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

        return Result.Success(read.ToImmutable());

        async Task<Result<Prefab>> LoadOneAsync(Handle<Prefab> prefab)
        {
            Result<Prefab> one = await _assets.LoadAsync(prefab, null, cancel).ConfigureAwait(false);
            progress?.Report(new LoadProgress(Work, Interlocked.Increment(ref done), places.Length));

            return one;
        }
    }

    /// <summary>
    /// The entities of the project's player, at the first of <paramref name="placed"/> tagged spawn: <paramref name="domain"/>
    /// doesn't own them, so they stay as it opens again, and go when another replaces it. None when the project has no player,
    /// or it doesn't load, which is an error.
    /// </summary>
    private async Task<ImmutableArray<ImmutableArray<IComponent>>> ReadPlayerAsync(
        Handle<Domain> domain,
        ImmutableArray<ImmutableArray<IComponent>> placed,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        Handle<Prefab> player = _app.Project?.Player ?? Handle<Prefab>.None;
        if (player == Handle<Prefab>.None)
            return [];

        Result<Prefab> loaded = await _assets.LoadAsync(player, progress, cancel).ConfigureAwait(false);
        if (!loaded.Ok)
        {
            Log.Error($"The project's player is not spawned: {loaded.Error}");

            return [];
        }

        // Where the player goes: the first of the placed tagged spawn, so a domain's globals before its chunks; else the
        // origin, which is warned about.
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
