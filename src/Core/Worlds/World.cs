using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Text.Json;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Files;
using Magic.Files.Pipeline;
using Magic.Files.Types;
using Magic.Serialization;
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
/// goes to the ECS a gem provides; for bulk work, fill a list of your own and <see cref="Apply"/> it. What comes and goes
/// does so one at a time, and each entity a domain placed is kept by its key, for saves and for when the domain goes. As the run starts it puts
/// back the world the last run kept, or opens the entry point, without waiting for it, as frames run while it loads; as the
/// run ends for a restart it keeps the world for the next; and it changes for what the assets say changed. Entity calls are for the main thread; the others, any.
/// </summary>
public sealed class World : IEcs, IDisposable
{
    private readonly Assets _assets;

    private readonly IFileSystem _files;

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
    /// Each entity's shading by the entity and its renderer's slot: one per material the renderer held as the entity was
    /// made, gone with it. Main thread.
    /// </summary>
    private readonly Dictionary<(Entity Entity, int Slot), ShadingInstance> _shading = [];

    /// <summary>
    /// The prefabs each open domain placed, as they were read: what its entities are compared with, by a save and as the
    /// domain opens again. Main thread.
    /// </summary>
    private readonly Dictionary<Handle<Domain>, ImmutableArray<PlacedPrefab>> _read = [];

    /// <summary>
    /// The ECS a gem provides, as soon as it is made.
    /// </summary>
    private IEcs _ecs = new NoEcs();

    /// <summary>
    /// Completed as the next frame starts, and replaced for the one after, by <see cref="Pump"/>: what a replace waits on
    /// frame by frame. Main thread.
    /// </summary>
    private TaskCompletionSource _frameStarting = new();

    /// <summary>
    /// The gems that hook into domain loading, in the order they were made. Main thread.
    /// </summary>
    private ImmutableArray<IHookIntoDomainLoading> _hooks = [];

    internal World(Assets assets, IFileSystem files, Threads threads, App app)
    {
        _assets = assets;
        _files = files;
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
    /// Whether a gem provides the ECS, so content can come into the world.
    /// </summary>
    private bool HasEcs => _ecs is not NoEcs;

    public void Dispose() => _turn.Dispose();

    /// <summary>
    /// Opens <paramref name="domain"/>, read first on workers, so one that doesn't load leaves the world as it was. One
    /// already open beside the others stays as it is. Why not, when it doesn't load or no gem provides the ECS.
    /// </summary>
    public async Task<Result> OpenAsync(
        Handle<Domain> domain,
        DomainOpenMode mode,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        Result opened = Result.Failure("it was not opened.");
        await RunLoadingAsync(
                async () => opened = await OpenDomainAsync(domain, mode, progress, cancel).ConfigureAwait(false),
                cancel
            )
            .ConfigureAwait(false);

        return opened.Ok ? opened : Result.Failure($"The domain does not open: {opened.Error}");
    }

    /// <summary>
    /// Takes <paramref name="domain"/> out of the world; nothing happens when it is not open.
    /// </summary>
    public Task CloseAsync(Handle<Domain> domain, CancellationToken cancel) =>
        RunInTurnAsync(
            () =>
            {
                if (Domains.Contains(domain))
                    Remove(domain);

                return Task.CompletedTask;
            },
            cancel
        );

    /// <summary>
    /// Puts <paramref name="prefab"/>, loaded first, in the world at <paramref name="at"/>: the entities it made, which are
    /// the caller's to destroy, and go when a domain replaces the world if they are still there. Why not, when it doesn't load
    /// or no gem provides the ECS. Single entities need none of this: make them with <see cref="Create()"/>.
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
        ImmutableArray<Entity> spawned = [];
        await RunInTurnAsync(
                () =>
                {
                    spawned = CreateEntities(coming);

                    return Task.CompletedTask;
                },
                cancel
            )
            .ConfigureAwait(false);

        return Result.Success(spawned);
    }

    /// <summary>
    /// A new entity with <paramref name="components"/>, made now. One <paramref name="makeGlobal"/> is tagged
    /// <see cref="Tag.Global"/> beside the tags they give it, and outlives every domain that replaces the world; throws when
    /// its tags have no room for that.
    /// </summary>
    public Entity Create(ReadOnlySpan<IComponent> components, bool makeGlobal = false)
    {
        ThrowIfNoEcs();
        Entity made = makeGlobal ? _ecs.Create(WithGlobalTag(components).AsSpan()) : _ecs.Create(components);
        CreateShading([made]);

        return made;
    }

    /// <summary>
    /// Saves the game as <c>Saves/<paramref name="name"/>.save</c> beside Magic: the domains open, what changed against
    /// their files, and the pause and time scale. Why not, when JSON can't hold it or it can't be written.
    /// </summary>
    public async Task<Result> SaveAsync(string name, CancellationToken cancel)
    {
        SaveFile? saving = null;
        await RunInTurnAsync(
                () =>
                {
                    saving = new SaveFile(Domains, FindChanges(Domains), IsPaused, TimeScale);

                    return Task.CompletedTask;
                },
                cancel
            )
            .ConfigureAwait(false);
        if (saving is null)
            return Result.Failure("the world was not read.");

        Result<JsonElement> json = await Task.Run(() => Json.Write(saving, typeof(SaveFile)), cancel)
            .ConfigureAwait(false);
        if (!json.Ok)
            return Result.Failure(json.Error);

        return await _files
            .WriteAsync(GetSavePath(name), Encoding.UTF8.GetBytes(json.Value.GetRawText()), cancel)
            .ConfigureAwait(false);
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

        Result<SaveFile> saved = Json.Read<SaveFile>(bytes.Value);
        if (!saved.Ok)
            return Result.Failure(saved.Error);

        Result loaded = Result.Failure("it was not loaded.");
        await RunLoadingAsync(
                async () => loaded = await OpenSaveAsync(saved.Value, cancel).ConfigureAwait(false),
                cancel
            )
            .ConfigureAwait(false);

        return loaded;
    }

    /// <summary>
    /// Makes <paramref name="commands"/> now, then empties it for reuse: for a list of your own, straight after a query's
    /// loop or a run. Throws during one, as the changes themselves would.
    /// </summary>
    public void Apply(EntityCommands commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ThrowIfNoEcs();
        commands.Apply(this);
        commands.Clear();
    }

    /// <inheritdoc/>
    public Entity Create() => Create([]);

    /// <inheritdoc/>
    public void Create<T1>(ReadOnlySpan<T1> first, Span<Entity> created)
        where T1 : unmanaged, IComponent
    {
        ThrowIfNoRoom(first.Length, created);
        ThrowIfNoEcs();
        _ecs.Create(first, created);
        CreateShading(created);
    }

    /// <inheritdoc/>
    public void Create<T1, T2>(ReadOnlySpan<T1> first, ReadOnlySpan<T2> second, Span<Entity> created)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
    {
        ThrowIfNoRoom(first.Length, created);
        ThrowIfNoEcs();
        _ecs.Create(first, second, created);
        CreateShading(created);
    }

    /// <inheritdoc/>
    public void Destroy(Entity entity)
    {
        ThrowIfDead(entity);
        _ecs.Destroy(entity);
        DestroyShading(entity);
    }

    /// <inheritdoc/>
    public bool IsAlive(Entity entity) => _ecs.IsAlive(entity);

    /// <summary>
    /// Gives the entity <paramref name="component"/>, or overwrites the one it has.
    /// </summary>
    public void Set<T>(Entity entity, in T component)
        where T : unmanaged, IComponent
    {
        ThrowIfDead(entity);
        _ecs.Set(entity, in component);
    }

    /// <summary>
    /// Gives the entity each of <paramref name="components"/>, or overwrites the one it has.
    /// </summary>
    public void Set(Entity entity, ReadOnlySpan<IComponent> components)
    {
        ThrowIfDead(entity);
        _ecs.Set(entity, components);
    }

    /// <inheritdoc/>
    public ref T Get<T>(Entity entity)
        where T : unmanaged, IComponent => ref _ecs.Get<T>(entity);

    /// <inheritdoc/>
    public bool Has<T>(Entity entity)
        where T : unmanaged, IComponent => _ecs.Has<T>(entity);

    /// <inheritdoc/>
    public void Remove<T>(Entity entity)
        where T : unmanaged, IComponent
    {
        ThrowIfDead(entity);
        if (_ecs.Has<T>(entity))
            _ecs.Remove<T>(entity);
    }

    /// <inheritdoc/>
    public IComponent[] GetComponents(Entity entity) => _ecs.GetComponents(entity);

    /// <summary>
    /// The entity's own shading in <paramref name="slot"/> of its renderer, made with the entity; null for an empty slot
    /// or an entity made without a renderer.
    /// </summary>
    public ShadingInstance? GetShading(Entity entity, int slot) => _shading.GetValueOrDefault((entity, slot));

    /// <inheritdoc/>
    public EcsQuery Query() => _ecs.Query();

    /// <inheritdoc/>
    public EcsQuery<T1> Query<T1>()
        where T1 : unmanaged, IComponent => _ecs.Query<T1>();

    /// <inheritdoc/>
    public EcsQuery<T1, T2> Query<T1, T2>()
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent => _ecs.Query<T1, T2>();

    /// <inheritdoc/>
    public EcsQuery<T1, T2, T3> Query<T1, T2, T3>()
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent => _ecs.Query<T1, T2, T3>();

    /// <inheritdoc/>
    public EcsQuery<T1, T2, T3, T4> Query<T1, T2, T3, T4>()
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent => _ecs.Query<T1, T2, T3, T4>();

    /// <inheritdoc/>
    public void Run<TState>(in TState state, EcsChunkRun<TState, EcsChunk> run) => _ecs.Run(in state, run);

    /// <inheritdoc/>
    public void Run<TState, T1>(in TState state, EcsChunkRun<TState, EcsChunk<T1>> run)
        where T1 : unmanaged, IComponent => _ecs.Run(in state, run);

    /// <inheritdoc/>
    public void Run<TState, T1, T2>(in TState state, EcsChunkRun<TState, EcsChunk<T1, T2>> run)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent => _ecs.Run(in state, run);

    /// <inheritdoc/>
    public void Run<TState, T1, T2, T3>(in TState state, EcsChunkRun<TState, EcsChunk<T1, T2, T3>> run)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent => _ecs.Run(in state, run);

    /// <inheritdoc/>
    public void Run<TState, T1, T2, T3, T4>(in TState state, EcsChunkRun<TState, EcsChunk<T1, T2, T3, T4>> run)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent => _ecs.Run(in state, run);

    /// <summary>
    /// Changes the world for the prefabs and domains the assets read again, and lets a replace waiting for this frame go on.
    /// Main thread, as each frame starts, before the gems' hooks.
    /// </summary>
    internal void Pump()
    {
        ProcessReloads(_assets.Reloaded, _app.Lifetime);
        TaskCompletionSource starting = _frameStarting;
        _frameStarting = new TaskCompletionSource();
        starting.SetResult();
    }

    internal Subscription Register(IEcs ecs)
    {
        _ecs = ecs;

        return new Subscription(() => _ecs = new NoEcs());
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

        foreach (EcsChunk<Transform, Spin> chunk in Query<Transform, Spin>())
        {
            for (int i = 0; i < chunk.Entities.Length; i++)
            {
                chunk.First[i].Rotation = chunk.Second[i].Turn(chunk.First[i].Rotation, deltaSec);
            }
        }
    }

    /// <summary>
    /// Puts back the JSON <paramref name="kept"/>; the entry point opens when there is none or it doesn't read, which is an
    /// error.
    /// </summary>
    internal async Task StartAsync(byte[]? kept, CancellationToken cancel)
    {
        if (kept is not null)
        {
            Result<SaveFile> read = Json.Read<SaveFile>(kept);
            Result loaded = Result.Failure(read.Error);
            if (read.Ok)
            {
                await RunLoadingAsync(
                        async () => loaded = await OpenSaveAsync(read.Value, cancel).ConfigureAwait(false),
                        cancel
                    )
                    .ConfigureAwait(false);
            }

            if (loaded.Ok)
                return;

            Log.Error($"The world goes back to the entry point: the kept world does not open: {loaded.Error}");
        }

        await OpenEntryPointAsync(cancel).ConfigureAwait(false);
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

        SaveFile saved = new(Domains, FindChanges(Domains), IsPaused, TimeScale);
        Result<JsonElement> json = Json.Write(saved, typeof(SaveFile));
        if (!json.Ok)
        {
            Log.Error($"The world goes back to the entry point: it could not be saved: {json.Error}");

            return null;
        }

        Log.Info($"Kept the world: {saved.Changes.Entities.Length} changes in {saved.Domains.Length} domains.");

        return Encoding.UTF8.GetBytes(json.Value.GetRawText());
    }

    /// <summary>
    /// <paramref name="components"/>, tagged global; throws when their tags have no room for that.
    /// </summary>
    private static ImmutableArray<IComponent> WithGlobalTag(ReadOnlySpan<IComponent> components)
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

    private static Tags AddGlobalTag(Tags tags)
    {
        if (tags.Has(Tag.Global) || tags.Add(Tag.Global))
            return tags;

        throw new ArgumentException(
            $"The entity's tags have no room to tag it global: an entity has at most {Tags.Capacity}.",
            nameof(tags)
        );
    }

    private static void ThrowIfNoRoom(int count, Span<Entity> created)
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
            Transform placed = PlaceUnder(part, parent);
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
    /// Where <paramref name="part"/> is under <paramref name="parent"/>: its transform placed there, or the parent's
    /// place when it has none.
    /// </summary>
    private static Transform PlaceUnder(PrefabPart part, Transform parent)
    {
        foreach (IComponent component in part.Components)
        {
            if (component is Transform local)
                return local.Under(parent);
        }

        return parent;
    }

    /// <summary>
    /// Where a player goes: the first of <paramref name="entities"/> tagged spawn, so a domain's globals before its chunks;
    /// else the origin, which is warned about.
    /// </summary>
    private static Transform FindSpawn(string path, ImmutableArray<ImmutableArray<IComponent>> entities)
    {
        foreach (ImmutableArray<IComponent> entity in entities)
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

            if (isSpawn && place is { } spawn)
                return new Transform { Position = spawn.Position, Rotation = spawn.Rotation };
        }

        Log.Warn($"{path} has no entity tagged spawn, so the player is put at its origin.");

        return new Transform();
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
    /// Runs <paramref name="work"/> on the main thread once no other work on the world is running.
    /// </summary>
    private async Task RunInTurnAsync(Func<Task> work, CancellationToken cancel)
    {
        await _turn.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            await _threads.Enter(EngineThread.Main);
            await work().ConfigureAwait(false);
        }
        finally
        {
            _turn.Release();
        }
    }

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
    /// <paramref name="domain"/>'s entities go, and it is no longer in the world. Main thread.
    /// </summary>
    private void Remove(Handle<Domain> domain)
    {
        Unplace(domain);
        _read.Remove(domain);
        Domains = Domains.Remove(domain);
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
            if (_ecs.IsAlive(_placed[key]))
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
            if (_ecs.IsAlive(entity))
                Destroy(entity);
        }
    }

    /// <summary>
    /// One <see cref="ShadingInstance"/> per filled slot of each entity's renderer, for those made with one. Main thread.
    /// </summary>
    private void CreateShading(ReadOnlySpan<Entity> entities)
    {
        foreach (Entity entity in entities)
        {
            if (!_ecs.Has<Renderer>(entity))
                continue;

            ReadOnlySpan<Handle<Material>> materials = _ecs.Get<Renderer>(entity).Materials;
            for (int slot = 0; slot < materials.Length; slot++)
            {
                if (materials[slot] == Handle<Material>.None)
                    continue;

                _shading[(entity, slot)] = new ShadingInstance(materials[slot]);
            }
        }
    }

    private void DestroyShading(Entity entity)
    {
        for (int slot = 0; slot < MaterialSlots.Capacity; slot++)
        {
            _shading.Remove((entity, slot));
        }
    }

    private ImmutableArray<Entity> FindAll()
    {
        ImmutableArray<Entity>.Builder all = ImmutableArray.CreateBuilder<Entity>();
        foreach (EcsChunk chunk in Query())
        {
            foreach (Entity entity in chunk.Entities)
            {
                all.Add(entity);
            }
        }

        return all.ToImmutable();
    }

    /// <summary>
    /// Runs <paramref name="change"/> as a change, which may set <see cref="IsLoading"/>, and ends with it unset.
    /// </summary>
    private Task RunLoadingAsync(Func<Task> change, CancellationToken cancel) =>
        RunInTurnAsync(
            async () =>
            {
                try
                {
                    await change().ConfigureAwait(false);
                }
                finally
                {
                    IsLoading = false;
                    IsShowingLoadingScreen = false;
                }
            },
            cancel
        );

    private string GetName(Handle<Domain> domain) => _assets.GetPath(domain) ?? "a domain";

    private void ThrowIfNoEcs()
    {
        if (!HasEcs)
            throw NoEcs.GetMissing();
    }

    private void ThrowIfDead(Entity entity)
    {
        if (!_ecs.IsAlive(entity))
            throw new InvalidOperationException($"{entity} is not alive.");
    }

    /// <summary>
    /// Opens again what places the prefabs and domains that were read again.
    /// </summary>
    private void ProcessReloads(ImmutableArray<AssetLoaded> reloaded, CancellationToken cancel)
    {
        if (reloaded.IsEmpty)
            return;

        HashSet<ulong> changed = [];
        foreach (AssetLoaded asset in reloaded)
        {
            if (asset.Asset is Prefab or Domain)
                changed.Add(asset.Id);
        }

        if (changed.Count > 0)
            ReopenAsync(changed, cancel).FireAndForget();
    }

    /// <summary>
    /// Reads the domain on workers, so one that doesn't load leaves the world as it was. One already open beside the others
    /// is just found. A replace comes with the project's player, through the loading screen (<see cref="ReplaceAsync"/>).
    /// </summary>
    private async Task<Result> OpenDomainAsync(
        Handle<Domain> domain,
        DomainOpenMode mode,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        if (!HasEcs)
            return Result.Failure("no gem the project lists provides the ECS.");

        bool isReplace = mode == DomainOpenMode.Replace;
        if (!isReplace && Domains.Contains(domain))
            return Result.Success();

        Result<ImmutableArray<PlacedPrefab>> read = await ReadDomainAsync(domain, progress, cancel)
            .ConfigureAwait(false);
        if (!read.Ok)
            return Result.Failure(read.Error);

        if (!isReplace)
        {
            await _threads.Enter(EngineThread.Main);
            Add(domain, read.Value);

            return Result.Success();
        }

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
        ImmutableArray<ImmutableArray<IComponent>> screen = await ReadLoadingScreenAsync(cancel).ConfigureAwait(false);
        await _threads.Enter(EngineThread.Main);
        Clear(isClearingGlobals);
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

        // Two frame starts on, a whole frame's hooks have drawn what came, wherever this went on in the frame.
        await WaitForFrameAsync(cancel).ConfigureAwait(false);
        await WaitForFrameAsync(cancel).ConfigureAwait(false);
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

        return new WorldChanges(_ecs, _placed, authored);
    }

    private string GetSavePath(string name) => _files.Combine(AppContext.BaseDirectory, "Saves", name + ".save");

    /// <summary>
    /// Opens the project's entry point in place of the world, or the default one when it names none or that doesn't
    /// open. With no ECS, nothing opens, which is warned about.
    /// </summary>
    private async Task OpenEntryPointAsync(CancellationToken cancel)
    {
        if (!HasEcs)
        {
            Log.Warn("No gem the project lists provides the ECS, so the world holds no entities, and no domain opens.");

            return;
        }

        Handle<Domain> fallback = _assets.GetDefault<Domain>();
        Handle<Domain> entry = _app.Project?.EntryPoint ?? Handle<Domain>.None;
        Handle<Domain> first = entry == Handle<Domain>.None ? fallback : entry;
        Result opened = await OpenAsync(first, DomainOpenMode.Replace, null, cancel).ConfigureAwait(false);
        if (opened.Ok)
            return;

        Log.Error(opened.Error);
        if (first == fallback)
            return;

        Result second = await OpenAsync(fallback, DomainOpenMode.Replace, null, cancel).ConfigureAwait(false);
        if (!second.Ok)
            Log.Error($"The default domain does not open either: {second.Error}");
    }

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

        string path = _assets.GetPath(handle) ?? "A domain";
        ImmutableArray<DomainPlace> places = GetPlaces(domain.Value);
        if (places.Length > 0 && !_assets.CanImport<Prefab>())
            return Result.Failure($"{path} holds prefabs, and no gem the project lists reads them.");

        Result<Prefab>[] prefabs = await LoadPrefabsAsync(places, progress, cancel).ConfigureAwait(false);
        ImmutableArray<PlacedPrefab>.Builder read = ImmutableArray.CreateBuilder<PlacedPrefab>(places.Length);
        for (int i = 0; i < places.Length; i++)
        {
            if (prefabs[i] is { Ok: true, Value: { } prefab })
                read.Add(new PlacedPrefab(places[i], prefab));
            else
                Log.Error($"{path} goes without a prefab: {prefabs[i].Error}");
        }

        return Result.Success(read.ToImmutable());
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

        return GetEntities(loaded.Value, FindSpawn(_assets.GetPath(domain) ?? "A domain", placed));
    }

    /// <summary>
    /// The prefabs of <paramref name="places"/>, loaded at once, telling how far that is, at the same indexes.
    /// </summary>
    private async Task<Result<Prefab>[]> LoadPrefabsAsync(
        ImmutableArray<DomainPlace> places,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        const string Work = "Reading prefabs";

        int done = 0;
        progress?.Report(new LoadProgress(Work, 0, places.Length));
        Task<Result<Prefab>>[] loading = new Task<Result<Prefab>>[places.Length];
        for (int i = 0; i < loading.Length; i++)
        {
            loading[i] = LoadOneAsync(places[i].Prefab);
        }

        return await Task.WhenAll(loading).ConfigureAwait(false);

        async Task<Result<Prefab>> LoadOneAsync(Handle<Prefab> prefab)
        {
            Result<Prefab> one = await _assets.LoadAsync(prefab, null, cancel).ConfigureAwait(false);
            progress?.Report(new LoadProgress(Work, Interlocked.Increment(ref done), places.Length));

            return one;
        }
    }

    /// <summary>
    /// The project's loading screen, else the engine's, a black void: the entities it is; none, which is warned about, when
    /// it doesn't load.
    /// </summary>
    private async Task<ImmutableArray<ImmutableArray<IComponent>>> ReadLoadingScreenAsync(CancellationToken cancel)
    {
        Handle<Domain> project = _app.Project?.LoadingScreen ?? Handle<Domain>.None;
        Handle<Domain> domain =
            project == Handle<Domain>.None ? _assets.Find<Domain>("Domains/Loading/Loading.domain") : project;
        if (domain == Handle<Domain>.None)
            return [];

        Result<ImmutableArray<PlacedPrefab>> read = await ReadDomainAsync(domain, null, cancel).ConfigureAwait(false);
        if (!read.Ok)
        {
            Log.Warn($"No loading screen is shown: {read.Error}");

            return [];
        }

        return GetComponents(GetPlacements(domain, read.Value));
    }

    /// <summary>
    /// Everything in the world leaves but its globals, those too with <paramref name="isClearingGlobals"/>. Main thread.
    /// </summary>
    private void Clear(bool isClearingGlobals)
    {
        ImmutableArray<Entity>.Builder leaving = ImmutableArray.CreateBuilder<Entity>();
        foreach (Entity entity in FindAll())
        {
            bool isGlobal = Has<Tags>(entity) && Get<Tags>(entity).Has(Tag.Global);
            if (isClearingGlobals || !isGlobal)
                leaving.Add(entity);
        }

        DestroyEntities(leaving.ToImmutable());
        _placed.Clear();
        _read.Clear();
        Domains = [];
    }

    /// <inheritdoc/>
    Entity IEcs.Create(ReadOnlySpan<IComponent> components) => Create(components);
}
