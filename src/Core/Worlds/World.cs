using System.Collections.Immutable;
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
/// The game world: what is in it, the one place to load content into it, and the way to its entities. Every entity call
/// goes to the ECS a gem provides; for bulk work, fill a list of your own and <see cref="Apply"/> it. What comes and goes
/// does so one change at a time, and what comes holds on to the entities it made, for when it goes. As the run starts it puts
/// back the world the last run kept, or opens the entry point, without waiting for it, as frames run while it loads; as the
/// run ends for a restart it keeps the world for the next; and it changes for what the assets say changed. Entity calls are for the main thread; the others, any.
/// </summary>
public sealed class World : IEcs, IDisposable
{
    private readonly Assets _assets;

    private readonly Threads _threads;

    private readonly App _app;

    /// <summary>
    /// Held for a change, so changes run one at a time.
    /// </summary>
    private readonly SemaphoreSlim _changing = new(1, 1);

    /// <summary>
    /// The entities each of <see cref="Domains"/> made, written as it comes and read as it goes. Main thread.
    /// </summary>
    private readonly Dictionary<Handle<Domain>, ImmutableArray<Entity>> _entitiesOf = [];

    /// <summary>
    /// The ECS a gem provides, as soon as it is made.
    /// </summary>
    private IEcs _ecs = new NoEcs();

    internal World(Assets assets, Threads threads, App app)
    {
        _assets = assets;
        _threads = threads;
        _app = app;
    }

    /// <summary>
    /// The domains open, in the order they came, without the loading screen.
    /// </summary>
    public ImmutableArray<Handle<Domain>> Domains { get; private set; } = [];

    /// <summary>
    /// Whether a domain is replacing what is in the world, or a saved world being put back.
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
    /// Whether a gem provides the ECS, so content can come into the world.
    /// </summary>
    internal bool HasEcs => _ecs is not NoEcs;

    /// <summary>
    /// The gem that provides the ECS, from when it is made.
    /// </summary>
    internal IEcs Ecs
    {
        set => _ecs = value;
    }

    public void Dispose() => _changing.Dispose();

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
        ChangeAsync(
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
        await ChangeAsync(
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

        return makeGlobal ? _ecs.Create(WithGlobalTag(components).AsSpan()) : _ecs.Create(components);
    }

    /// <summary>
    /// The world as it is, once no other change is running: what <see cref="RestoreAsync"/> takes. Save it as a <c>.world</c>
    /// asset with <see cref="Assets.SaveAsync{T}"/>.
    /// </summary>
    public async Task<WorldState> SaveAsync(CancellationToken cancel)
    {
        WorldState? saved = null;
        await ChangeAsync(
                () =>
                {
                    saved = Save();

                    return Task.CompletedTask;
                },
                cancel
            )
            .ConfigureAwait(false);

        return saved ?? throw new InvalidOperationException("The world was not saved.");
    }

    /// <summary>
    /// Puts the world back as <paramref name="saved"/> holds it, in place of everything in it, globals too.
    /// </summary>
    public Task RestoreAsync(WorldState saved, CancellationToken cancel) =>
        RunLoadingAsync(
            () =>
            {
                Restore(saved);

                return Task.CompletedTask;
            },
            cancel
        );

    /// <summary>
    /// Makes <paramref name="changes"/> now, then empties it for reuse: for a list of your own, straight after a query's loop
    /// or a run. Throws during one, as the changes themselves would.
    /// </summary>
    public void Apply(EntitiesChangeList changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ThrowIfNoEcs();
        changes.Apply(_ecs);
        changes.Clear();
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
    }

    /// <inheritdoc/>
    public void Create<T1, T2>(ReadOnlySpan<T1> first, ReadOnlySpan<T2> second, Span<Entity> created)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
    {
        ThrowIfNoRoom(first.Length, created);
        ThrowIfNoEcs();
        _ecs.Create(first, second, created);
    }

    /// <inheritdoc/>
    public void Destroy(Entity entity)
    {
        ThrowIfDead(entity);
        _ecs.Destroy(entity);
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
    /// Changes the world for the prefabs and domains the assets read again. Main thread.
    /// </summary>
    internal void Pump(CancellationToken cancel) => ProcessReloads(_assets.Reloaded, cancel);

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
    internal async Task LoadAsync(byte[]? kept, CancellationToken cancel)
    {
        if (kept is not null)
        {
            Result<WorldState> read = Json.Read<WorldState>(kept);
            if (read.Ok)
            {
                await RestoreAsync(read.Value, cancel).ConfigureAwait(false);

                return;
            }

            Log.Error($"The world goes back to the entry point: the saved world does not read: {read.Error}");
        }

        await OpenEntryPointAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// The world as JSON for the next run, with it whole; none while it is still arriving, so the next run opens the entry
    /// point, or when JSON can't hold it. Main thread.
    /// </summary>
    internal byte[]? Keep()
    {
        if (IsLoading || Domains.IsEmpty)
            return null;

        WorldState saved = Save();
        Result<JsonElement> json = Json.Write(saved, typeof(WorldState));
        if (!json.Ok)
        {
            Log.Error($"The world goes back to the entry point: it could not be saved: {json.Error}");

            return null;
        }

        Log.Info($"Kept the world: {saved.EntityCount} entities in {saved.Domains.Length} domains.");

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

    /// <summary>
    /// The world as data: the domains open with the entities they made, every other entity, and its pause and time scale,
    /// as of now. Main thread, with no change running: <see cref="SaveAsync"/> waits for that; the run's end has it.
    /// </summary>
    private WorldState Save()
    {
        ImmutableArray<DomainState>.Builder domains = ImmutableArray.CreateBuilder<DomainState>(Domains.Length);
        HashSet<Entity> owned = [];
        foreach (Handle<Domain> domain in Domains)
        {
            ImmutableArray<Entity> entities = _entitiesOf.GetValueOrDefault(domain, []);
            owned.UnionWith(entities);
            domains.Add(new DomainState { Domain = domain, Entities = GetComponents(entities) });
        }

        ImmutableArray<Entity> rest = HasEcs ? [.. FindAll().Where(entity => !owned.Contains(entity))] : [];

        return new WorldState
        {
            Domains = domains.MoveToImmutable(),
            Entities = GetComponents(rest),
            IsPaused = IsPaused,
            TimeScale = TimeScale,
        };
    }

    /// <summary>
    /// Runs <paramref name="change"/> on the main thread when no other is running.
    /// </summary>
    private async Task ChangeAsync(Func<Task> change, CancellationToken cancel)
    {
        await _changing.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            await _threads.Enter(EngineThread.Main);
            await change().ConfigureAwait(false);
        }
        finally
        {
            _changing.Release();
        }
    }

    /// <summary>
    /// <paramref name="domain"/> comes, and is in the world from then on. Main thread.
    /// </summary>
    private void Add(Handle<Domain> domain, ImmutableArray<ImmutableArray<IComponent>> coming)
    {
        _entitiesOf[domain] = CreateEntities(coming);
        Domains = Domains.Add(domain);
        Log.Info($"Opened {GetName(domain)}: {coming.Length} entities.");
    }

    /// <summary>
    /// <paramref name="domain"/>'s entities go, and it is no longer in the world. Main thread.
    /// </summary>
    private void Remove(Handle<Domain> domain)
    {
        DestroyEntities(_entitiesOf.GetValueOrDefault(domain, []));
        _entitiesOf.Remove(domain);
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
            created.Add(_ecs.Create(components.AsSpan()));
        }

        return created.MoveToImmutable();
    }

    /// <summary>
    /// <paramref name="entities"/> go, those not gone already. Main thread.
    /// </summary>
    private void DestroyEntities(ImmutableArray<Entity> entities)
    {
        foreach (Entity entity in entities)
        {
            if (_ecs.IsAlive(entity))
                _ecs.Destroy(entity);
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
    /// Each of <paramref name="entities"/> still alive, as its components.
    /// </summary>
    private ImmutableArray<ImmutableArray<IComponent>> GetComponents(ImmutableArray<Entity> entities)
    {
        ImmutableArray<ImmutableArray<IComponent>>.Builder components = ImmutableArray.CreateBuilder<
            ImmutableArray<IComponent>
        >(entities.Length);
        foreach (Entity entity in entities)
        {
            if (_ecs.IsAlive(entity))
                components.Add([.. _ecs.GetComponents(entity)]);
        }

        return components.ToImmutable();
    }

    /// <summary>
    /// Runs <paramref name="change"/> as a change, which may set <see cref="IsLoading"/>, and ends with it unset.
    /// </summary>
    private Task RunLoadingAsync(Func<Task> change, CancellationToken cancel) =>
        ChangeAsync(
            async () =>
            {
                try
                {
                    await change().ConfigureAwait(false);
                }
                finally
                {
                    IsLoading = false;
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
    /// is just found. A replace shows the loading screen and clears the world around bringing it in.
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

        Result<ImmutableArray<ImmutableArray<IComponent>>> read = await ReadDomainAsync(domain, progress, cancel)
            .ConfigureAwait(false);
        if (!read.Ok)
            return Result.Failure(read.Error);

        ImmutableArray<ImmutableArray<IComponent>> player = isReplace
            ? await ReadPlayerAsync(domain, read.Value, progress, cancel).ConfigureAwait(false)
            : [];
        ImmutableArray<Entity> loading = [];
        if (isReplace)
        {
            IsLoading = true;
            loading = await ShowLoadingScreenAsync(cancel).ConfigureAwait(false);
        }

        await _threads.Enter(EngineThread.Main);
        if (isReplace)
            Clear(loading);

        Add(domain, read.Value);
        CreateEntities(player);
        DestroyEntities(loading);

        return Result.Success();
    }

    /// <summary>
    /// Everything leaves, globals too, and what the saved world holds comes: each domain with its own entities, then the
    /// rest. Main thread.
    /// </summary>
    private void Restore(WorldState saved)
    {
        if (!HasEcs)
        {
            Log.Warn("The world is not restored: no gem the project lists provides the ECS.");

            return;
        }

        IsLoading = true;
        DestroyEntities(FindAll());
        _entitiesOf.Clear();
        foreach (DomainState kept in saved.Domains)
        {
            _entitiesOf[kept.Domain] = CreateEntities(kept.Entities);
        }

        CreateEntities(saved.Entities);
        Domains = [.. saved.Domains.Select(kept => kept.Domain)];
        IsPaused = saved.IsPaused;
        TimeScale = saved.TimeScale;
        Log.Info($"Restored the world: {saved.EntityCount} entities in {saved.Domains.Length} domains.");
    }

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
    /// Each open domain that is or places one of <paramref name="changed"/> makes its entities again, losing what happened
    /// to them; one that no longer loads stays as it was, which is an error.
    /// </summary>
    private Task ReopenAsync(HashSet<ulong> changed, CancellationToken cancel) =>
        ChangeAsync(
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

    private async Task ReopenAsync(Handle<Domain> domain, CancellationToken cancel)
    {
        Result<ImmutableArray<ImmutableArray<IComponent>>> read = await ReadDomainAsync(domain, null, cancel)
            .ConfigureAwait(false);
        await _threads.Enter(EngineThread.Main);
        if (!read.Ok)
        {
            Log.Error($"{GetName(domain)} is not opened again: {read.Error}");

            return;
        }

        DestroyEntities(_entitiesOf.GetValueOrDefault(domain, []));
        _entitiesOf[domain] = CreateEntities(read.Value);
        Log.Info($"Opened {GetName(domain)} again: {read.Value.Length} entities.");
    }

    /// <summary>
    /// The entities of <paramref name="handle"/>, each prefab it places made into them. A prefab that doesn't load is an
    /// error, and it is left out; why not, when the domain doesn't load or no gem reads prefabs.
    /// </summary>
    private async Task<Result<ImmutableArray<ImmutableArray<IComponent>>>> ReadDomainAsync(
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
        ImmutableArray<ImmutableArray<IComponent>>.Builder entities = ImmutableArray.CreateBuilder<
            ImmutableArray<IComponent>
        >();
        for (int i = 0; i < places.Length; i++)
        {
            if (prefabs[i] is { Ok: true, Value: { } prefab })
                entities.AddRange(GetEntities(prefab, places[i].At));
            else
                Log.Error($"{path} goes without a prefab: {prefabs[i].Error}");
        }

        return Result.Success(entities.ToImmutable());
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
    /// Brings in the project's loading screen, else the engine's: the entities it is; none, which is warned about, when it
    /// doesn't load.
    /// </summary>
    private async Task<ImmutableArray<Entity>> ShowLoadingScreenAsync(CancellationToken cancel)
    {
        Handle<Domain> project = _app.Project?.LoadingScreen ?? Handle<Domain>.None;
        Handle<Domain> domain =
            project == Handle<Domain>.None ? _assets.Find<Domain>("Domains/Loading/Loading.domain") : project;
        if (domain == Handle<Domain>.None)
            return [];

        Result<ImmutableArray<ImmutableArray<IComponent>>> read = await ReadDomainAsync(domain, null, cancel)
            .ConfigureAwait(false);
        if (!read.Ok)
        {
            Log.Warn($"No loading screen is shown: {read.Error}");

            return [];
        }

        await _threads.Enter(EngineThread.Main);

        return CreateEntities(read.Value);
    }

    /// <summary>
    /// Everything in the world leaves but its globals and <paramref name="keeping"/>.
    /// </summary>
    private void Clear(ImmutableArray<Entity> keeping)
    {
        ImmutableArray<Entity>.Builder leaving = ImmutableArray.CreateBuilder<Entity>();
        foreach (Entity entity in FindAll())
        {
            bool isGlobal = Has<Tags>(entity) && Get<Tags>(entity).Has(Tag.Global);
            if (!isGlobal && !keeping.Contains(entity))
                leaving.Add(entity);
        }

        DestroyEntities(leaving.ToImmutable());
        _entitiesOf.Clear();
        Domains = [];
    }

    /// <inheritdoc/>
    Entity IEcs.Create(ReadOnlySpan<IComponent> components) => Create(components);
}
