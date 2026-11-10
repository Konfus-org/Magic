using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.JsonDiffPatch;
using System.Text.Json.Nodes;
using Magic.Assets;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Events;
using Magic.Files;
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
/// The game world: what is in it, the one place to load content into it, and the way to its entities. Every entity call
/// goes to the ECS a gem provides. What comes and goes
/// does so one at a time, and each entity a domain placed has its <see cref="Owner"/>, for saves and for when the domain opens again. As the run starts it puts
/// back the world the last run kept, or opens the entry point, without waiting for it, as frames run while it loads; as the
/// run ends for a restart it keeps the world for the next; and it changes for what the assets say changed. Entity calls are for the game thread; the others, any.
/// </summary>
public sealed class World : IEcs, IDisposable
{
    private readonly AssetManager _assets;

    private readonly IFileSystem _files;

    private readonly EventCoordinator _events;

    private readonly Threads _threads;

    private readonly App _app;

    /// <summary>
    /// Held while work on the world runs, so it runs in turn.
    /// </summary>
    private readonly SemaphoreSlim _turn = new(1, 1);

    /// <summary>
    /// Each open domain and the prefabs it placed, as they were read: what its entities are compared with, by a save and as
    /// the domain opens again; held, so the assets read them again when their files change. Game thread.
    /// </summary>
    private readonly Dictionary<Handle<Domain>, DomainRead> _read = [];

    /// <summary>
    /// The ECS a gem provides, from when it is made until it goes; null while none does.
    /// </summary>
    private IEcs? _ecs;

    /// <summary>
    /// Completed as the next frame starts, and replaced for the one after, by <see cref="Pump"/>: what a replace waits on
    /// frame by frame. Game thread.
    /// </summary>
    private TaskCompletionSource _frameStarting = new();

    /// <summary>
    /// The gems that hook into domain loading, in the order they were made. Game thread.
    /// </summary>
    private ImmutableArray<IHookIntoDomainLoading> _hooks = [];

    internal World(AssetManager assets, IFileSystem files, EventCoordinator events, Threads threads, App app)
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
    /// Set on the game thread, read on any.
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
    /// The game as a save holds it, as UTF-8 JSON: the domains open, and how the world, with its pause and time scale,
    /// differs from their files. The world is taken now, and the diff found on a worker. Why not, while the world is
    /// loading or has no domain open, or when JSON can't hold it. Game thread.
    /// </summary>
    public Task<Result<byte[]>> SaveAsync()
    {
        if (IsLoading || Domains.IsEmpty)
            return Task.FromResult<Result<byte[]>>(
                Result.Failure(IsLoading ? "the world is loading." : "no domain is open.")
            );

        ImmutableArray<DomainRead> domains = GetOpenDomains();
        WorldState now = GetState();

        return Task.Run(() => ToSave(domains, now));
    }

    /// <summary>
    /// Saves the game as <c>Magic/&lt;project&gt;/Saves/<paramref name="name"/>.save</c> under the user's local
    /// application data, as <see cref="SaveAsync()"/> holds it, once no other work on the world runs. Why not, when that has none or it can't be written.
    /// </summary>
    public async Task<Result> SaveAsync(string name, CancellationToken cancel)
    {
        Task<Result<byte[]>> saving = await RunInTurnAsync(() => Task.FromResult(SaveAsync()), cancel)
            .ConfigureAwait(false);
        Result<byte[]> bytes = await saving.ConfigureAwait(false);
        if (!bytes.Ok)
            return Result.Failure(bytes.Error);

        return await _files.WriteAsync(GetSavePath(name), bytes.Value, cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Puts the game back as the save <paramref name="name"/> holds it, in place of everything in the world,
    /// globals too: its domains as their files make them, with its changes made on them. Why not, when there is no such save, it
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
    /// a replace waiting for this frame go on. Game thread, as each frame starts, before the gems' hooks.
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
    /// Has <paramref name="ecs"/> hold the world's entities, until the subscription is disposed. Game thread.
    /// </summary>
    internal Subscription Register(IEcs ecs)
    {
        _ecs = ecs;

        return new Subscription(() => _ecs = null);
    }

    /// <summary>
    /// Has <paramref name="hooks"/> take part as domains replace the world, until the subscription is disposed. Game thread.
    /// </summary>
    internal Subscription Register(IHookIntoDomainLoading hooks)
    {
        _hooks = _hooks.Add(hooks);

        return new Subscription(() => _hooks = _hooks.Remove(hooks));
    }

    /// <summary>
    /// Every entity with a <see cref="Spin"/> turned on by <paramref name="deltaSec"/> of the world's time. Game thread.
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
    /// Puts back the world as the last run saved it, <paramref name="restorePoint"/>, once that is done. The entry point opens
    /// when there is none, when the save failed, which is warned about, or when it doesn't open, which is an error: the
    /// project's, or the default one when it names none or that doesn't open. With no ECS, nothing opens, which is warned
    /// about.
    /// </summary>
    internal async Task StartAsync(Task<Result<byte[]>>? restorePoint, CancellationToken cancel)
    {
        if (restorePoint is not null)
        {
            Result<byte[]> saved = await restorePoint.ConfigureAwait(false);
            Result restored = saved.Ok ? await RestoreAsync(saved.Value, cancel).ConfigureAwait(false) : saved;
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
    /// <paramref name="value"/> as UTF-8 JSON; why not, when JSON can't hold it.
    /// </summary>
    private static Result<byte[]> ToBytes<T>(T value)
    {
        Result<JsonElement> json = Json.Write(value, typeof(T));

        return json.Ok ? Result.Success(Encoding.UTF8.GetBytes(json.Value.GetRawText())) : Result.Failure(json.Error);
    }

    /// <summary>
    /// The save of <paramref name="now"/>: <paramref name="domains"/>, and how it differs from what their files make, as
    /// UTF-8 JSON; why not, when JSON can't hold it.
    /// </summary>
    private static Result<byte[]> ToSave(ImmutableArray<DomainRead> domains, WorldState now)
    {
        Result<JsonNode?> changes = Diff(GetBaseState(domains), now);

        return changes.Ok
            ? ToBytes(new SaveFile([.. domains.Select(domain => domain.Domain)], changes.Value))
            : Result.Failure(changes.Error);
    }

    /// <summary>
    /// How <paramref name="to"/> differs from <paramref name="from"/>, as a JSON diff; none when it doesn't, and why not,
    /// when JSON can't hold either.
    /// </summary>
    private static Result<JsonNode?> Diff(WorldState from, WorldState to)
    {
        Result<byte[]> was = ToBytes(from);
        Result<byte[]> now = ToBytes(to);
        if (!was.Ok || !now.Ok)
            return Result.Failure(was.Ok ? now.Error : was.Error);

        return Result.Success(JsonDiffPatcher.Diff(was.Value, now.Value));
    }

    /// <summary>
    /// <paramref name="state"/> with <paramref name="changes"/>, a JSON diff, made on it: a change to an entity or a
    /// component <paramref name="state"/> doesn't hold, as its files no longer make it, is skipped. Why not, when the result
    /// doesn't read as a world.
    /// </summary>
    private static Result<WorldState> Patch(WorldState state, JsonNode? changes)
    {
        if (changes is null)
            return Result.Success(state);

        Result<byte[]> bytes = ToBytes(state);
        if (!bytes.Ok)
            return Result.Failure(bytes.Error);

        JsonNode fitting = changes.DeepClone();
        if (fitting["placed"] is JsonObject placed)
        {
            RemoveMissing(placed, state.Placed.ContainsKey);
            foreach ((string key, JsonNode? change) in placed)
            {
                if (
                    change is JsonObject components
                    && state.Placed.TryGetValue(key, out IReadOnlyDictionary<string, IComponent>? was)
                )
                    RemoveMissing(components, was.ContainsKey);
            }
        }

        JsonNode? patched = JsonDiffPatcher.PatchNew(JsonNode.Parse(bytes.Value), fitting);

        return Json.Read<WorldState>(JsonSerializer.SerializeToUtf8Bytes(patched));
    }

    /// <summary>
    /// Takes out of <paramref name="changes"/> each change, but an add, to what <paramref name="has"/> says isn't there.
    /// </summary>
    private static void RemoveMissing(JsonObject changes, Func<string, bool> has)
    {
        foreach ((string name, JsonNode? change) in changes.ToArray())
        {
            // A diff's add is an array of the one value added.
            if (change is not JsonArray { Count: 1 } && !has(name))
                changes.Remove(name);
        }
    }

    /// <summary>
    /// The world as <paramref name="domains"/>' files make it, with its time running.
    /// </summary>
    private static WorldState GetBaseState(ImmutableArray<DomainRead> domains) =>
        new(
            domains
                .SelectMany(domain => GetEntities(domain))
                .ToDictionary(
                    entity => GetKey(entity.OfType<Owner>().Single()),
                    IReadOnlyDictionary<string, IComponent> (entity) => ToByType(entity)
                ),
            [],
            IsPaused: false,
            TimeScale: 1
        );

    private static Dictionary<string, IComponent> ToByType(IEnumerable<IComponent> components) =>
        components.ToDictionary(component => component.GetType().Name);

    /// <summary>
    /// <paramref name="owner"/>'s entity's key in <see cref="WorldState.Placed"/>.
    /// </summary>
    private static string GetKey(Owner owner) => $"{owner.Domain.Id}/{owner.Prefab.Id}/{owner.Index}";

    /// <summary>
    /// The entities <paramref name="state"/> holds, those a domain placed first.
    /// </summary>
    private static ImmutableArray<ImmutableArray<IComponent>> GetEntities(WorldState state) =>
        [.. state.Placed.Values.Concat(state.Made).Select(entity => entity.Values.ToImmutableArray())];

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
    /// <paramref name="domain"/> comes, and is in the world from then on. Game thread.
    /// </summary>
    private void Add(DomainRead domain)
    {
        ImmutableArray<Entity> coming = CreateEntities(GetEntities(domain));
        _read[domain.Domain] = domain;
        Domains = Domains.Add(domain.Domain);
        Log.Info($"Opened {GetName(domain.Domain)}: {coming.Length} entities.");
    }

    /// <summary>
    /// One entity for each of <paramref name="entities"/>. Game thread.
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
    /// <paramref name="entities"/> go, those not gone already. Game thread.
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
    /// The domains open, with the prefabs each placed as they were read. Game thread.
    /// </summary>
    private ImmutableArray<DomainRead> GetOpenDomains() => [.. Domains.Select(domain => _read[domain])];

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

        Result<DomainRead> read = await ReadDomainAsync(domain, progress, cancel).ConfigureAwait(false);
        if (!read.Ok)
            return Result.Failure(read.Error);

        DomainRead opened = read.Value;
        ImmutableArray<ImmutableArray<IComponent>> placed = GetEntities(opened);
        ImmutableArray<ImmutableArray<IComponent>> player = await ReadPlayerAsync(domain, placed, progress, cancel)
            .ConfigureAwait(false);
        await ReplaceAsync(
                [.. placed, .. player],
                isClearingGlobals: false,
                () =>
                {
                    Add(opened);
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
            Result<DomainRead> read = await ReadDomainAsync(screenDomain, null, cancel).ConfigureAwait(false);
            if (read.Ok)
                screen = GetEntities(read.Value);
            else
                Log.Warn($"No loading screen is shown: {read.Error}");
        }

        // Everything in the world leaves but its globals, those too with isClearingGlobals. A global that stays outlives
        // the domain that owned it, so it is owned by none, as an entity made as the game runs.
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
        await RunHooksAsync(hooks => hooks.OnDomainUnloadedAsync(cancel), "cleaning up").ConfigureAwait(false);

        await _threads.Enter(EngineThread.Game);
        ImmutableArray<Entity> loading = CreateEntities(screen);
        IsShowingLoadingScreen = true;
        await RunHooksAsync(hooks => hooks.OnDomainLoadingAsync(arriving, cancel), "preparing").ConfigureAwait(false);

        // A gem that doesn't hook into domain loading may still be loading what comes.
        while (_assets.HasLoadsInFlight)
        {
            await WaitForFrameAsync(cancel).ConfigureAwait(false);
        }

        await _threads.Enter(EngineThread.Game);
        DestroyEntities(loading);
        await RunHooksAsync(hooks => hooks.OnDomainUnloadedAsync(cancel), "cleaning up").ConfigureAwait(false);

        // The low-latency GC Program asks for seldom collects gen 2, so what the load and the unloads threw away would
        // stay committed: one collection that compacts and decommits gives it back, before anything of the world shows.
        await _threads.Enter(EngineThread.Game);
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
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
    /// Waits for the next frame to start, and goes on there, on the game thread.
    /// </summary>
    private async Task WaitForFrameAsync(CancellationToken cancel)
    {
        await _frameStarting.Task.WaitAsync(cancel).ConfigureAwait(false);
        await _threads.Enter(EngineThread.Game);
    }

    /// <summary>
    /// Everything leaves, globals too, and <paramref name="saved"/> comes: its domains as their files make them, read
    /// first on workers, so one that doesn't open leaves the world as it was, with its changes made on them.
    /// </summary>
    private async Task<Result> OpenSaveAsync(SaveFile saved, CancellationToken cancel)
    {
        if (!HasEcs)
            return Result.Failure("no gem the project lists provides the ECS.");

        ImmutableArray<DomainRead>.Builder reading = ImmutableArray.CreateBuilder<DomainRead>(saved.Domains.Length);
        foreach (Handle<Domain> domain in saved.Domains)
        {
            Result<DomainRead> read = await ReadDomainAsync(domain, null, cancel).ConfigureAwait(false);
            if (!read.Ok)
                return Result.Failure($"{GetName(domain)} does not open: {read.Error}");

            reading.Add(read.Value);
        }

        ImmutableArray<DomainRead> domains = reading.MoveToImmutable();
        Result<WorldState> state = await Task.Run(() => Patch(GetBaseState(domains), saved.Changes), cancel)
            .ConfigureAwait(false);
        if (!state.Ok)
            return Result.Failure($"its changes don't fit its domains' files: {state.Error}");

        ImmutableArray<ImmutableArray<IComponent>> entities = GetEntities(state.Value);
        await ReplaceAsync(
                entities,
                isClearingGlobals: true,
                () =>
                {
                    foreach (DomainRead domain in domains)
                    {
                        _read[domain.Domain] = domain;
                        Domains = Domains.Add(domain.Domain);
                    }

                    CreateEntities(entities);
                    IsPaused = state.Value.IsPaused;
                    TimeScale = state.Value.TimeScale;
                    Log.Info($"Put the world back: {entities.Length} entities in {domains.Length} domains.");
                },
                cancel
            )
            .ConfigureAwait(false);

        return Result.Success();
    }

    private string GetSavePath(string name) =>
        _files.Combine(
            UserFolders.GetProjectFolder(
                _files,
                _app.Project ?? throw new InvalidOperationException("Nothing is saved until a project is open.")
            ),
            "Saves",
            name + ".save"
        );

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
    /// <paramref name="domain"/>'s entities made again from its files as they are now, with what the game changed in them
    /// against its files as they were made on top: a value the game changed keeps it, one it didn't takes the file's, and a
    /// destroyed entity stays destroyed.
    /// </summary>
    private async Task ReopenAsync(Handle<Domain> domain, CancellationToken cancel)
    {
        Result<DomainRead> read = await ReadDomainAsync(domain, null, cancel).ConfigureAwait(false);
        await _threads.Enter(EngineThread.Game);
        if (!read.Ok)
        {
            Log.Error($"{GetName(domain)} is not opened again: {read.Error}");

            return;
        }

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

        Result<JsonNode?> changes = Diff(
            GetBaseState([_read[domain]]),
            new WorldState(now, [], IsPaused: false, TimeScale: 1)
        );
        Result<WorldState> kept = changes.Ok
            ? Patch(GetBaseState([read.Value]), changes.Value)
            : Result.Failure(changes.Error);
        if (!kept.Ok)
        {
            Log.Error($"{GetName(domain)} is not opened again: {kept.Error}");

            return;
        }

        DestroyEntities(leaving.ToImmutable());
        ImmutableArray<Entity> made = CreateEntities(GetEntities(kept.Value));
        _read[domain] = read.Value;
        Log.Info($"Opened {GetName(domain)} again: {made.Length} entities, with what changed in them kept.");
    }

    /// <summary>
    /// <paramref name="handle"/>, loaded, with the prefabs it places, loaded, and where. A prefab that doesn't load is an
    /// error, and it is left out; why not, when the domain doesn't load or no gem reads prefabs.
    /// </summary>
    private async Task<Result<DomainRead>> ReadDomainAsync(
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
