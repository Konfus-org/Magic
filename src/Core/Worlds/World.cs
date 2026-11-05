using System.Collections.Immutable;
using System.Linq;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Files;
using Magic.Files.Pipeline;
using Magic.Files.Types;
using Magic.Projects;
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
/// does so one change at a time, and where an entity came from is on it (<see cref="Origin"/>). As the run starts it puts
/// back the world the last run kept, or opens the entry point, without waiting for it, as frames run while it loads; as the
/// run ends for a restart it keeps the world for the next; and it changes for what the assets say changed. Entity calls are for the main thread; the others, any.
/// </summary>
public sealed class World : IEcs, IDisposable
{
    /// <summary>
    /// The content of what no content placed, among a saved world's entities.
    /// </summary>
    private const ulong NoContent = 0;

    private readonly Assets _assets;

    private readonly Threads _threads;

    private readonly App _app;

    private readonly Assemblies _assemblies;

    /// <summary>
    /// Held for a change, so changes run one at a time.
    /// </summary>
    private readonly SemaphoreSlim _changing = new(1, 1);

    private ulong _lastContentId;

    /// <summary>
    /// The ECS a gem provides, as soon as it is made.
    /// </summary>
    private IEcs _ecs = new NoEcs();

    internal World(Assets assets, Threads threads, App app, Assemblies assemblies)
    {
        _assets = assets;
        _threads = threads;
        _assemblies = assemblies;
        _app = app;
    }

    /// <summary>
    /// What is in the world, in the order it came, without the loading screen.
    /// </summary>
    public ImmutableArray<WorldContent> Contents { get; private set; } = [];

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
    public async Task<Result<WorldContent>> OpenAsync(
        Handle<Domain> domain,
        DomainOpenMode mode,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        WorldContent content = new(GetNextContentId(), domain);
        Result<WorldContent> opened = Result.Failure("it was not opened.");
        await RunLoadingAsync(
                async () =>
                    opened = await OpenContentAsync(domain, mode, content, progress, cancel).ConfigureAwait(false),
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
                if (FindOpen(domain) is { } open)
                    Remove(open);

                return Task.CompletedTask;
            },
            cancel
        );

    /// <summary>
    /// Puts <paramref name="prefab"/>, loaded first, in the world at <paramref name="at"/>. Why not, when it doesn't load or
    /// no gem provides the ECS. Single entities need none of this: make them with <see cref="Create()"/>.
    /// </summary>
    public async Task<Result<WorldContent>> SpawnAsync(
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

        WorldContent content = new(GetNextContentId(), Handle<Domain>.None);
        ImmutableArray<ImmutableArray<IComponent>> coming = GetEntities(loaded.Value, at, prefab, content);
        await ChangeAsync(
                () =>
                {
                    Add(content, coming);

                    return Task.CompletedTask;
                },
                cancel
            )
            .ConfigureAwait(false);

        return Result.Success(content);
    }

    /// <summary>
    /// Takes what <see cref="SpawnAsync"/> put in the world back out.
    /// </summary>
    public Task UnspawnAsync(WorldContent spawned, CancellationToken cancel) =>
        ChangeAsync(
            () =>
            {
                Remove(spawned);

                return Task.CompletedTask;
            },
            cancel
        );

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
    /// The world as plain JSON, once no other change is running: what <see cref="RestoreAsync"/> takes, to keep where you
    /// like. Why not, when JSON can't hold it.
    /// </summary>
    public async Task<Result<byte[]>> SaveAsync(CancellationToken cancel)
    {
        Result<byte[]> saved = Result.Failure("it was not saved.");
        await ChangeAsync(
                () =>
                {
                    saved = Save().ToJson();

                    return Task.CompletedTask;
                },
                cancel
            )
            .ConfigureAwait(false);

        return saved;
    }

    /// <summary>
    /// Puts the world back as the JSON <paramref name="saved"/> holds it, in place of everything in it, globals too. Why not,
    /// when it isn't a saved world.
    /// </summary>
    public async Task<Result> RestoreAsync(ReadOnlyMemory<byte> saved, CancellationToken cancel)
    {
        Result<WorldState> read = WorldState.FromJson(saved.Span, _assemblies);
        if (!read.Ok)
            return Result.Failure(read.Error);

        await RunLoadingAsync(
                () =>
                {
                    Restore(read.Value);

                    return Task.CompletedTask;
                },
                cancel
            )
            .ConfigureAwait(false);

        return Result.Success();
    }

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
    /// Puts back the JSON <paramref name="kept"/>; the entry point opens when there is none or it doesn't read, which is an
    /// error.
    /// </summary>
    internal async Task LoadAsync(byte[]? kept, CancellationToken cancel)
    {
        if (kept is not null)
        {
            Result restored = await RestoreAsync(kept, cancel).ConfigureAwait(false);
            if (restored.Ok)
                return;

            Log.Error($"The world goes back to the entry point: the saved world does not read: {restored.Error}");
        }

        await OpenEntryPointAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// The world as JSON for the next run, with it whole; none while it is still arriving, so the next run opens the entry
    /// point, or when JSON can't hold it. Main thread.
    /// </summary>
    internal byte[]? Keep()
    {
        if (IsLoading || Contents.IsEmpty)
            return null;

        WorldState saved = Save();
        Result<byte[]> json = saved.ToJson();
        if (!json.Ok)
        {
            Log.Error($"The world goes back to the entry point: it could not be saved: {json.Error}");

            return null;
        }

        Log.Info($"Kept the world: {saved.Entities.Length} entities in {saved.Contents.Length} contents.");

        return json.Value;
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
    /// placed under its parent's, and the top parts' under where it is put, each saying it is <paramref name="content"/>'s,
    /// from <paramref name="handle"/>, put at <paramref name="at"/>.
    /// </summary>
    private static ImmutableArray<ImmutableArray<IComponent>> GetEntities(
        Prefab prefab,
        Transform at,
        Handle<Prefab> handle,
        WorldContent content
    )
    {
        Origin origin = new()
        {
            Domain = content.Domain,
            Content = content.Id,
            Prefab = handle,
            At = at,
        };
        ImmutableArray<ImmutableArray<IComponent>>.Builder entities = ImmutableArray.CreateBuilder<
            ImmutableArray<IComponent>
        >();
        AddEntities(prefab.Parts, at, origin, entities);

        return entities.ToImmutable();
    }

    private static void AddEntities(
        ImmutableArray<PrefabPart> parts,
        Transform parent,
        Origin origin,
        ImmutableArray<ImmutableArray<IComponent>>.Builder entities
    )
    {
        foreach (PrefabPart part in parts)
        {
            Transform placed = PlaceUnder(part, parent);
            ImmutableArray<IComponent>.Builder components = ImmutableArray.CreateBuilder<IComponent>(
                part.Components.Length + 1
            );
            foreach (IComponent component in part.Components)
            {
                components.Add(component is Transform ? placed : component);
            }

            components.Add(origin);
            entities.Add(components.MoveToImmutable());
            AddEntities(part.Children, placed, origin, entities);
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
    private static Transform FindSpawn(string path, ImmutableArray<ImmutableArray<IComponent>>.Builder entities)
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

    private static bool IsAt(Origin origin, DomainPlace place) =>
        origin.Prefab == place.Prefab && origin.At == place.At;

    private static Dictionary<ulong, ImmutableArray<ImmutableArray<IComponent>>.Builder> GroupByContent(
        WorldState state
    )
    {
        Dictionary<ulong, ImmutableArray<ImmutableArray<IComponent>>.Builder> byContent = [];
        foreach (ImmutableArray<IComponent> entity in state.Entities)
        {
            ulong id = NoContent;
            foreach (IComponent component in entity)
            {
                if (component is Origin origin)
                    id = origin.Content;
            }

            if (!byContent.TryGetValue(id, out ImmutableArray<ImmutableArray<IComponent>>.Builder? entities))
            {
                entities = ImmutableArray.CreateBuilder<ImmutableArray<IComponent>>();
                byContent.Add(id, entities);
            }

            entities.Add(entity);
        }

        return byContent;
    }

    private static ImmutableArray<ImmutableArray<IComponent>> GetEntities(
        Dictionary<ulong, ImmutableArray<ImmutableArray<IComponent>>.Builder> byContent,
        ulong id
    ) =>
        byContent.TryGetValue(id, out ImmutableArray<ImmutableArray<IComponent>>.Builder? entities)
            ? entities.ToImmutable()
            : [];

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
    /// The world as data: what is in it, every entity with its components, and its pause and time scale, as of now. Main
    /// thread, with no change running: <see cref="SaveAsync"/> waits for that; the run's end has it.
    /// </summary>
    private WorldState Save() =>
        new()
        {
            Contents = Contents,
            Entities = HasEcs ? GetAllEntities() : [],
            IsPaused = IsPaused,
            TimeScale = TimeScale,
        };

    private ulong GetNextContentId() => Interlocked.Increment(ref _lastContentId);

    /// <summary>
    /// Has content ids go on from <paramref name="id"/> at least, as what was put back holds it.
    /// </summary>
    private void KeepContentIdsPast(ulong id) => Interlocked.Exchange(ref _lastContentId, Math.Max(_lastContentId, id));

    private WorldContent? FindOpen(Handle<Domain> domain)
    {
        foreach (WorldContent content in Contents)
        {
            if (content.Domain == domain)
                return content;
        }

        return null;
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
    /// <paramref name="content"/> comes, and is in the world from then on. Main thread.
    /// </summary>
    private void Add(WorldContent content, ImmutableArray<ImmutableArray<IComponent>> coming)
    {
        CreateEntities(coming);
        Contents = Contents.Add(content);
        Log.Info($"Opened {GetName(content)}: {coming.Length} entities.");
    }

    /// <summary>
    /// <paramref name="content"/>'s entities go, and it is no longer in the world. Main thread.
    /// </summary>
    private void Remove(WorldContent content)
    {
        DestroyEntities(FindOf(content.Id));
        Contents = Contents.Remove(content);
    }

    /// <summary>
    /// One entity for each of <paramref name="entities"/>. Main thread.
    /// </summary>
    private void CreateEntities(ImmutableArray<ImmutableArray<IComponent>> entities)
    {
        foreach (ImmutableArray<IComponent> components in entities)
        {
            _ecs.Create(components.AsSpan());
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
    /// The entities of the content <paramref name="id"/>.
    /// </summary>
    private ImmutableArray<Entity> FindOf(ulong id)
    {
        ImmutableArray<Entity>.Builder found = ImmutableArray.CreateBuilder<Entity>();
        foreach ((ReadOnlySpan<Entity> entities, Span<Origin> origins) in Query<Origin>())
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
    /// Every entity, as its components.
    /// </summary>
    private ImmutableArray<ImmutableArray<IComponent>> GetAllEntities()
    {
        ImmutableArray<Entity> all = FindAll();
        ImmutableArray<ImmutableArray<IComponent>>.Builder entities = ImmutableArray.CreateBuilder<
            ImmutableArray<IComponent>
        >(all.Length);
        foreach (Entity entity in all)
        {
            entities.Add([.. _ecs.GetComponents(entity)]);
        }

        return entities.MoveToImmutable();
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

    private string GetName(WorldContent content) =>
        content.Domain == Handle<Domain>.None ? $"content {content.Id}" : _assets.GetPath(content.Domain) ?? "a domain";

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
    /// Changes the world for the prefabs and domains that were read again.
    /// </summary>
    private void ProcessReloads(ImmutableArray<AssetLoaded> reloaded, CancellationToken cancel)
    {
        foreach (AssetLoaded asset in reloaded)
        {
            if (asset.Asset is Prefab prefab)
                ReloadPrefabAsync(new Handle<Prefab>(asset.Id), prefab, cancel).FireAndForget();
            else if (asset.Asset is Domain domain)
                ApplyDomainAsync(new Handle<Domain>(asset.Id), domain, cancel).FireAndForget();
        }
    }

    /// <summary>
    /// Reads the domain on workers, so one that doesn't load leaves the world as it was. One already open beside the others
    /// is just found. A replace shows the loading screen and clears the world around bringing it in.
    /// </summary>
    private async Task<Result<WorldContent>> OpenContentAsync(
        Handle<Domain> domain,
        DomainOpenMode mode,
        WorldContent content,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        if (!HasEcs)
            return Result.Failure("no gem the project lists provides the ECS.");

        bool isReplace = mode == DomainOpenMode.Replace;
        if (!isReplace && FindOpen(domain) is { } open)
            return Result.Success(open);

        Handle<Prefab> player = isReplace ? _app.Project?.Player ?? Handle<Prefab>.None : Handle<Prefab>.None;
        Result<ImmutableArray<ImmutableArray<IComponent>>> read = await ReadDomainAsync(
                content,
                player,
                progress,
                cancel
            )
            .ConfigureAwait(false);
        if (!read.Ok)
            return Result.Failure(read.Error);

        WorldContent? loading = null;
        if (isReplace)
        {
            IsLoading = true;
            loading = await ShowLoadingScreenAsync(cancel).ConfigureAwait(false);
        }

        await _threads.Enter(EngineThread.Main);
        if (isReplace)
            Clear();

        Add(content, read.Value);
        if (loading is { } shown)
            DestroyEntities(FindOf(shown.Id));

        return Result.Success(content);
    }

    /// <summary>
    /// Everything leaves, globals too, and what the saved world holds comes: each content with its own entities, then those
    /// made as the game ran. Main thread.
    /// </summary>
    private void Restore(WorldState saved)
    {
        if (!HasEcs)
        {
            Log.Warn("The world is not restored: no gem the project lists provides the ECS.");

            return;
        }

        IsLoading = true;
        foreach (WorldContent content in Contents)
        {
            Remove(content);
        }

        DestroyEntities(FindAll());

        Dictionary<ulong, ImmutableArray<ImmutableArray<IComponent>>.Builder> byContent = GroupByContent(saved);
        foreach (WorldContent content in saved.Contents)
        {
            KeepContentIdsPast(content.Id);
            CreateEntities(GetEntities(byContent, content.Id));
        }

        CreateEntities(GetEntities(byContent, NoContent));
        Contents = saved.Contents;
        IsPaused = saved.IsPaused;
        TimeScale = saved.TimeScale;
        Log.Info($"Restored the world: {saved.Entities.Length} entities in {saved.Contents.Length} contents.");
    }

    /// <summary>
    /// The entities made as the game ran rather than placed, but its globals: what goes as a domain replaces the world.
    /// </summary>
    private ImmutableArray<Entity> FindRuntime()
    {
        ImmutableArray<Entity>.Builder runtime = ImmutableArray.CreateBuilder<Entity>();
        foreach (Entity entity in FindAll())
        {
            bool isGlobal = Has<Tags>(entity) && Get<Tags>(entity).Has(Tag.Global);
            if (!isGlobal && !Has<Origin>(entity))
                runtime.Add(entity);
        }

        return runtime.ToImmutable();
    }

    /// <summary>
    /// The entities that came from one placing of a prefab, as <paramref name="origin"/> says.
    /// </summary>
    private ImmutableArray<Entity> FindOf(Origin origin)
    {
        ImmutableArray<Entity>.Builder found = ImmutableArray.CreateBuilder<Entity>();
        foreach ((ReadOnlySpan<Entity> entities, Span<Origin> origins) in Query<Origin>())
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
    private ImmutableArray<Origin> FindOrigins(Handle<Prefab> prefab)
    {
        HashSet<Origin> found = [];
        foreach ((ReadOnlySpan<Entity> _, Span<Origin> origins) in Query<Origin>())
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
    private ImmutableArray<Origin> FindOrigins(WorldContent content)
    {
        HashSet<Origin> found = [];
        foreach ((ReadOnlySpan<Entity> _, Span<Origin> origins) in Query<Origin>())
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
        Result<WorldContent> opened = await OpenAsync(first, DomainOpenMode.Replace, null, cancel)
            .ConfigureAwait(false);
        if (opened.Ok)
            return;

        Log.Error(opened.Error);
        if (first == fallback)
            return;

        Result<WorldContent> second = await OpenAsync(fallback, DomainOpenMode.Replace, null, cancel)
            .ConfigureAwait(false);
        if (!second.Ok)
            Log.Error($"The default domain does not open either: {second.Error}");
    }

    /// <summary>
    /// A prefab was read again: wherever its entities are, they leave and it comes again there as it is now.
    /// </summary>
    private Task ReloadPrefabAsync(Handle<Prefab> handle, Prefab prefab, CancellationToken cancel) =>
        ChangeAsync(
            () =>
            {
                foreach (Origin origin in FindOrigins(handle))
                {
                    WorldContent content = new(origin.Content, origin.Domain);
                    DestroyEntities(FindOf(origin));
                    CreateEntities(GetEntities(prefab, origin.At, handle, content));
                }

                return Task.CompletedTask;
            },
            cancel
        );

    /// <summary>
    /// A domain was read again: if it is open, what it no longer places where it was leaves, and what it now places
    /// where nothing was comes.
    /// </summary>
    private Task ApplyDomainAsync(Handle<Domain> handle, Domain domain, CancellationToken cancel) =>
        ChangeAsync(
            async () =>
            {
                if (FindOpen(handle) is not { } content)
                    return;

                ImmutableArray<DomainPlace> places = GetPlaces(domain);
                ImmutableArray<Origin> origins = FindOrigins(content);
                foreach (Origin origin in origins)
                {
                    if (!places.Any(place => IsAt(origin, place)))
                        DestroyEntities(FindOf(origin));
                }

                foreach (DomainPlace place in places)
                {
                    if (!origins.Any(origin => IsAt(origin, place)))
                        await PlaceAsync(content, place, cancel).ConfigureAwait(false);
                }
            },
            cancel
        );

    /// <summary>
    /// Brings the prefab <paramref name="place"/> names into <paramref name="content"/>; one that doesn't load is an error,
    /// and is tried again the next time the domain is read.
    /// </summary>
    private async Task PlaceAsync(WorldContent content, DomainPlace place, CancellationToken cancel)
    {
        Result<Prefab> prefab = await _assets.LoadAsync(place.Prefab, null, cancel).ConfigureAwait(false);
        await _threads.Enter(EngineThread.Main);
        if (!prefab.Ok)
        {
            Log.Error($"{_assets.GetPath(content.Domain)} goes without a prefab: {prefab.Error}");

            return;
        }

        CreateEntities(GetEntities(prefab.Value, place.At, place.Prefab, content));
    }

    /// <summary>
    /// The entities of the domain <paramref name="content"/> names, each prefab it places made into them, with the
    /// <paramref name="player"/> when there is one at the first entity tagged spawn. A prefab that doesn't load is an error,
    /// and it is left out; why not, when the domain doesn't load or no gem reads prefabs.
    /// </summary>
    private async Task<Result<ImmutableArray<ImmutableArray<IComponent>>>> ReadDomainAsync(
        WorldContent content,
        Handle<Prefab> player,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        Result<Domain> domain = await _assets.LoadAsync(content.Domain, progress, cancel).ConfigureAwait(false);
        if (!domain.Ok)
            return Result.Failure(domain.Error);

        string path = _assets.GetPath(content.Domain) ?? "A domain";
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
                entities.AddRange(GetEntities(prefab, places[i].At, places[i].Prefab, content));
            else
                Log.Error($"{path} goes without a prefab: {prefabs[i].Error}");
        }

        if (player == Handle<Prefab>.None)
            return Result.Success(entities.ToImmutable());

        Result<Prefab> loaded = await _assets.LoadAsync(player, progress, cancel).ConfigureAwait(false);
        if (loaded.Ok)
            entities.AddRange(GetEntities(loaded.Value, FindSpawn(path, entities), Handle<Prefab>.None, content));
        else
            Log.Error($"The project's player is not spawned: {loaded.Error}");

        return Result.Success(entities.ToImmutable());
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
    /// Brings in the project's loading screen, else the engine's: what it is in the world; none, which is warned about, when
    /// it doesn't load.
    /// </summary>
    private async Task<WorldContent?> ShowLoadingScreenAsync(CancellationToken cancel)
    {
        Handle<Domain> project = _app.Project?.LoadingScreen ?? Handle<Domain>.None;
        Handle<Domain> domain =
            project == Handle<Domain>.None ? _assets.Find<Domain>("Domains/Loading/Loading.domain") : project;
        if (domain == Handle<Domain>.None)
            return null;

        WorldContent loading = new(GetNextContentId(), domain);
        Result<ImmutableArray<ImmutableArray<IComponent>>> read = await ReadDomainAsync(
                loading,
                Handle<Prefab>.None,
                null,
                cancel
            )
            .ConfigureAwait(false);
        if (!read.Ok)
        {
            Log.Warn($"No loading screen is shown: {read.Error}");

            return null;
        }

        await _threads.Enter(EngineThread.Main);
        CreateEntities(read.Value);

        return loading;
    }

    /// <summary>
    /// Everything in the world leaves but its globals and the loading screen.
    /// </summary>
    private void Clear()
    {
        foreach (WorldContent content in Contents)
        {
            Remove(content);
        }

        DestroyEntities(FindRuntime());
    }

    /// <inheritdoc/>
    Entity IEcs.Create(ReadOnlySpan<IComponent> components) => Create(components);
}
