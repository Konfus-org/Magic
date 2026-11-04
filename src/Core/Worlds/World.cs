using System.Collections.Immutable;
using Magic.Assets;
using Magic.Assets.Pipeline;
using Magic.Assets.Types;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Pipes;
using Magic.Threading;
using Magic.Utils;

namespace Magic.Worlds;

/// <summary>
/// The game world: what is in it, the one place to load content into it, and the way to its entities. Every entity call
/// goes to the ECS a gem provides, and what changes which entities there are, or which components they have, runs the
/// pipeline over an <see cref="EntitiesChangeList"/> at once; for bulk work, push onto that pipeline's <c>Deferred</c>.
/// What loads or unloads runs its pipeline, one change at a time, and where an entity came from is on it
/// (<see cref="Origin"/>). Entity calls are for the main thread; the others, any.
/// </summary>
public sealed class World : IEcs, IDisposable
{
    private readonly AssetDatabase _assets;

    private readonly Threads _threads;

    private readonly Pipelines _pipelines;

    private readonly PipelineRunner<EntitiesChangeList> _entities;

    /// <summary>
    /// Held for a change, so changes run one at a time.
    /// </summary>
    private readonly SemaphoreSlim _changing = new(1, 1);

    private ulong _lastContentId;

    /// <summary>
    /// The ECS a gem provides, as soon as it is made.
    /// </summary>
    private IEcs _ecs = new NoEcs();

    internal World(AssetDatabase assets, Threads threads, Pipelines pipelines)
    {
        _assets = assets;
        _threads = threads;
        _pipelines = pipelines;
        _entities = pipelines.Get<EntitiesChangeList>();
    }

    /// <summary>
    /// What is in the world, in the order it came, without the loading screen.
    /// </summary>
    public ImmutableArray<WorldContent> Contents { get; internal set; } = [];

    /// <summary>
    /// Whether a domain is replacing what is in the world, or a saved world being put back.
    /// </summary>
    public bool IsLoading { get; internal set; }

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

    /// <summary>
    /// The gem that writes saved worlds, from when it is made; none when no gem the project lists does.
    /// </summary>
    internal IWorldStateWriter? Writer { get; set; }

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
        WorldOpen open = new(domain, mode, new WorldContent(GetNextContentId(), domain), [], progress, string.Empty);
        await RunLoadingAsync(
                async () =>
                    open = await _pipelines
                        .Get<WorldOpen>()
                        .RunOnMainAsync(open, progress, cancel)
                        .ConfigureAwait(false),
                cancel
            )
            .ConfigureAwait(false);

        return open.Error.Length > 0
            ? Result.Failure($"The domain does not open: {open.Error}")
            : Result.Success(open.Content);
    }

    /// <summary>
    /// Takes <paramref name="domain"/> out of the world; nothing happens when it is not open.
    /// </summary>
    public Task CloseAsync(Handle<Domain> domain, IProgress<LoadProgress>? progress, CancellationToken cancel) =>
        ChangeAsync(
            async () =>
            {
                if (FindOpen(domain) is { } open)
                    await RemoveAsync(open, progress, cancel).ConfigureAwait(false);
            },
            cancel
        );

    /// <summary>
    /// Puts <paramref name="asset"/>, loaded first, in the world at <paramref name="at"/>, as the entities the pipeline
    /// over its <see cref="WorldSpawn{T}"/> makes it into. Why not, when it doesn't load, nothing joined that pipeline or
    /// no gem provides the ECS. Single entities need none of this: make them with <see cref="Create()"/>.
    /// </summary>
    public async Task<Result<WorldContent>> SpawnAsync<T>(
        Handle<T> asset,
        Transform at,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
        where T : IAsset<T>
    {
        if (!HasEcs)
            return Result.Failure("Nothing spawns: no gem the project lists provides the ECS.");

        Result<T> loaded = await _assets.LoadAsync(asset, progress, cancel).ConfigureAwait(false);
        if (!loaded.Ok)
            return Result.Failure(loaded.Error);

        // A prefab's entities say so, for when it is read again.
        Handle<Prefab> prefab = typeof(T) == typeof(Prefab) ? new Handle<Prefab>(asset.Id) : Handle<Prefab>.None;
        WorldContent content = new(GetNextContentId(), Handle<Domain>.None);
        ImmutableArray<EntityTemplate> coming = await GetEntitiesAsync(loaded.Value, at, prefab, content, cancel)
            .ConfigureAwait(false);
        if (coming.IsDefault)
            return Result.Failure(
                $"Nothing spawns a {typeof(T).Name}: no gem the project lists makes entities of one."
            );

        await ChangeAsync(() => AddAsync(content, coming, progress, cancel), cancel).ConfigureAwait(false);

        return Result.Success(content);
    }

    /// <summary>
    /// Takes what <see cref="SpawnAsync"/> put in the world back out.
    /// </summary>
    public Task UnspawnAsync(WorldContent spawned, CancellationToken cancel) =>
        ChangeAsync(() => RemoveAsync(spawned, null, cancel), cancel);

    /// <summary>
    /// A new entity with <paramref name="components"/>, made now through the pipeline. One <paramref name="makeGlobal"/> is
    /// tagged <see cref="Tag.Global"/> beside the tags they give it, and outlives every domain that replaces the world;
    /// throws when its tags have no room for that.
    /// </summary>
    public Entity Create(ReadOnlySpan<IComponent> components, bool makeGlobal = false)
    {
        EntitiesChangeList changes = RentList();
        changes.Create([new EntityTemplate(makeGlobal ? WorldEntities.WithGlobalTag(components) : [.. components])]);
        _entities.Run(changes);
        Entity created = changes.Created[0];
        _entities.Return(changes);

        return created;
    }

    /// <summary>
    /// The world as data: what is in it, every entity with its components, and its pause and time scale, as of one moment
    /// on the main thread.
    /// </summary>
    public async Task<WorldState> SaveAsync(CancellationToken cancel)
    {
        WorldState saved = new() { Contents = [], Entities = [] };
        await ChangeAsync(
                () =>
                {
                    saved = new WorldState
                    {
                        Contents = Contents,
                        Entities = HasEcs ? WorldEntities.GetTemplates(this) : [],
                        IsPaused = IsPaused,
                        TimeScale = TimeScale,
                    };

                    return Task.CompletedTask;
                },
                cancel
            )
            .ConfigureAwait(false);

        return saved;
    }

    /// <summary>
    /// Puts the world back as <paramref name="state"/> holds it, in place of everything in it, globals too: each gem
    /// loads its part as for anything else that comes.
    /// </summary>
    public Task RestoreAsync(WorldState state, IProgress<LoadProgress>? progress, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(state);

        return RunLoadingAsync(
            async () =>
                await _pipelines
                    .Get<WorldRestore>()
                    .RunOnMainAsync(new WorldRestore(state, progress), progress, cancel)
                    .ConfigureAwait(false),
            cancel
        );
    }

    /// <summary>
    /// Puts the world back as the saved world <paramref name="saved"/>, loaded first, holds it; why not, when it doesn't load.
    /// </summary>
    public async Task<Result> RestoreAsync(
        Handle<WorldState> saved,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        Result<WorldState> loaded = await _assets.LoadAsync(saved, progress, cancel).ConfigureAwait(false);
        if (!loaded.Ok)
            return Result.Failure(loaded.Error);

        await RestoreAsync(loaded.Value, progress, cancel).ConfigureAwait(false);

        return Result.Success();
    }

    /// <summary>
    /// Runs the pipeline over <paramref name="changes"/> now, then empties it for reuse: for a list of your own, straight
    /// after a query's loop or a run. Throws during one, as the changes themselves would.
    /// </summary>
    public void Apply(EntitiesChangeList changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ThrowIfNoEcs();
        _entities.Run(changes);
        changes.Clear();
    }

    /// <inheritdoc/>
    public Entity Create() => Create([]);

    /// <inheritdoc/>
    public void Create<T1>(ReadOnlySpan<T1> first, Span<Entity> created)
        where T1 : unmanaged, IComponent
    {
        WorldEntities.ThrowIfNoRoom(first.Length, created);
        EntitiesChangeList changes = RentList();
        changes.Create(first);
        _entities.Run(changes);
        changes.Created.CopyTo(created);
        _entities.Return(changes);
    }

    /// <inheritdoc/>
    public void Create<T1, T2>(ReadOnlySpan<T1> first, ReadOnlySpan<T2> second, Span<Entity> created)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
    {
        WorldEntities.ThrowIfNoRoom(first.Length, created);
        EntitiesChangeList changes = RentList();
        changes.Create(first, second);
        _entities.Run(changes);
        changes.Created.CopyTo(created);
        _entities.Return(changes);
    }

    /// <inheritdoc/>
    public void Destroy(Entity entity)
    {
        ThrowIfDead(entity);
        EntitiesChangeList changes = RentList();
        changes.Destroy([entity]);
        RunList(changes);
    }

    /// <inheritdoc/>
    public bool IsAlive(Entity entity) => _ecs.IsAlive(entity);

    /// <summary>
    /// Gives the entity <paramref name="component"/>, or overwrites the one it has. Overwriting changes no entity's shape,
    /// so it goes straight to the ECS, and is fine during a loop; giving it one it lacks runs the pipeline.
    /// </summary>
    public void Set<T>(Entity entity, in T component)
        where T : unmanaged, IComponent
    {
        if (_ecs.Has<T>(entity))
        {
            _ecs.Set(entity, in component);

            return;
        }

        ThrowIfDead(entity);
        EntitiesChangeList changes = RentList();
        changes.Set(entity, in component);
        RunList(changes);
    }

    /// <summary>
    /// Gives the entity each of <paramref name="components"/>, or overwrites the one it has, running the pipeline.
    /// </summary>
    public void Set(Entity entity, ReadOnlySpan<IComponent> components)
    {
        ThrowIfDead(entity);
        EntitiesChangeList changes = RentList();
        changes.Set(entity, components);
        RunList(changes);
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
        if (!_ecs.Has<T>(entity))
            return;

        EntitiesChangeList changes = RentList();
        changes.Remove<T>(entity);
        RunList(changes);
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

    internal ulong GetNextContentId() => Interlocked.Increment(ref _lastContentId);

    /// <summary>
    /// Has content ids go on from <paramref name="id"/> at least, as what was put back holds it.
    /// </summary>
    internal void KeepContentIdsPast(ulong id) =>
        Interlocked.Exchange(ref _lastContentId, Math.Max(_lastContentId, id));

    internal WorldContent? FindOpen(Handle<Domain> domain)
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
    internal async Task ChangeAsync(Func<Task> change, CancellationToken cancel)
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
    /// What <paramref name="asset"/> spawns as, each entity saying it is <paramref name="content"/>'s, from
    /// <paramref name="prefab"/>, put at <paramref name="at"/>; left default when nothing makes entities of a
    /// <typeparamref name="T"/>.
    /// </summary>
    internal async Task<ImmutableArray<EntityTemplate>> GetEntitiesAsync<T>(
        T asset,
        Transform at,
        Handle<Prefab> prefab,
        WorldContent content,
        CancellationToken cancel
    )
        where T : IAsset<T>
    {
        WorldSpawn<T> spawn = await _pipelines
            .Get<WorldSpawn<T>>()
            .RunAsync(new WorldSpawn<T>(asset, at, default), cancel)
            .ConfigureAwait(false);

        return spawn.Entities.IsDefault ? default : WorldEntities.WithOrigin(spawn.Entities, at, prefab, content);
    }

    /// <summary>
    /// What leaves runs <see cref="WorldUnload"/>. On the main thread again after.
    /// </summary>
    internal async Task LeaveAsync(
        WorldContent content,
        ImmutableArray<Entity> leaving,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        await _pipelines
            .Get<WorldUnload>()
            .RunOnMainInReverseAsync(new WorldUnload(content, leaving, progress), progress, cancel)
            .ConfigureAwait(false);
        await _threads.Enter(EngineThread.Main);
    }

    /// <summary>
    /// What comes runs <see cref="WorldLoad"/>: how many entities came. On the main thread again after.
    /// </summary>
    internal async Task<int> ComeAsync(
        WorldContent content,
        ImmutableArray<EntityTemplate> coming,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        WorldLoad done = await _pipelines
            .Get<WorldLoad>()
            .RunOnMainAsync(new WorldLoad(content, coming, [], progress), progress, cancel)
            .ConfigureAwait(false);
        await _threads.Enter(EngineThread.Main);

        return done.Arrived.Length;
    }

    /// <summary>
    /// <paramref name="content"/> comes, and is in the world from then on.
    /// </summary>
    internal async Task AddAsync(
        WorldContent content,
        ImmutableArray<EntityTemplate> coming,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        int arrived = await ComeAsync(content, coming, progress, cancel).ConfigureAwait(false);
        Contents = Contents.Add(content);
        Log.Info($"Opened {GetName(content)}: {arrived} entities.");
    }

    internal async Task RemoveAsync(WorldContent content, IProgress<LoadProgress>? progress, CancellationToken cancel)
    {
        await LeaveAsync(content, WorldEntities.FindOf(this, content.Id), progress, cancel).ConfigureAwait(false);
        Contents = Contents.Remove(content);
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

    /// <summary>
    /// A list to run now; with no ECS nothing would make what is on it, so it throws.
    /// </summary>
    private EntitiesChangeList RentList()
    {
        ThrowIfNoEcs();

        return _entities.Rent();
    }

    private void RunList(EntitiesChangeList changes)
    {
        _entities.Run(changes);
        _entities.Return(changes);
    }

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

    /// <inheritdoc/>
    Entity IEcs.Create(ReadOnlySpan<IComponent> components) => Create(components);
}
