using System.Runtime.InteropServices;
using System.Text.Json;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.ECS;
using Magic.Contexts.Frames;
using Magic.Contexts.Subscriptions;
using Magic.Contexts.Threading;
using Magic.Contexts.Utils;
using Magic.Interfaces;
using Magic.Internal;

namespace Magic.Services;

/// <summary>
/// The game world: everything in it, and the one place to act on it. It opens and closes domains, puts assets in the
/// world (<see cref="Spawn"/>), makes, finds and changes entities and registers systems, over the pieces underneath,
/// which stay separate: the ECS a gem provides (<see cref="IEcs"/>), the scripts a gem runs (<see cref="IScripts"/>), and
/// the assets. Core always makes it, and opens the project's entry point through it on the first frame. Without a gem
/// that provides the ECS it holds no entities: no domain opens, which is warned about, and a call that needs entities
/// throws, saying so. Main thread, but where a member says otherwise.
/// <para>
/// A domain is a folder holding a <c>.domain</c> file (<see cref="Domain"/>) beside the prefabs it spawns: every one in
/// the folder, read on workers with the models they draw, then all spawned in one frame, a cell, named <c>x_y_z</c>, at
/// its corner, <see cref="Domain.CellSize"/> metres a side, and any other at the domain's origin. Nothing is loaded by
/// distance: whatever draws the world picks each thing's detail by its distance. Opening a domain in place of the
/// others (<see cref="DomainOpenMode.Replace"/>) closes them and shows the project's loading domain
/// (<see cref="Project.LoadingDomain"/>) until the new one is there, spawned with the project's player
/// (<see cref="Project.Player"/>) at its spawn (<see cref="Tag.Spawn"/>) in the frame the loading domain closes; the
/// window keeps the loading domain's last image while every gem that needs to readies the new one out of sight
/// (<see cref="IPreloader.IsReady"/>), and it is shown once they all have, drawn as it loaded, before anything moves
/// it. Where all that stands is <see cref="Stream"/>. Opening one beside them (<see cref="DomainOpenMode.Additive"/>)
/// shows no loading domain and puts no player.
/// Closing a domain destroys what it spawned. While it is open, a prefab of it read again is spawned again as it is now,
/// and prefabs added, moved or removed in its folder are spawned or destroyed.
/// </para>
/// <para>
/// The world empties each time something takes its place: as a domain begins to replace it, as that domain arrives,
/// the loading domain closing, and as a saved world is put back. Every entity no domain shown spawned is destroyed,
/// what scripts and gems made among them, but its globals (<see cref="CreateGlobal"/>), which only a saved world put
/// back destroys, and every gem that needs to forgets what it built up from the old world as it sees the world emptied
/// (<see cref="WorldStream.IsEmptied"/>).
/// </para>
/// <para>
/// A script attached to an entity (<see cref="Attach"/>) joins its <see cref="Scripts"/>, and the gem that runs
/// scripts makes and starts it in the next frame, so it never sees half a prefab or half a domain. While a domain loads
/// in place of the world, it starts as the domain is readied, so what it spawns is readied with it, and runs from the
/// frame after the world is first shown, unless its class is <see cref="Attributes.ImpatientAttribute">[Impatient]</see>
/// and it starts while the loading domain shows, as the loading domain's own scripts do.
/// </para>
/// <para>
/// The world has a clock of its own: it can be paused (<see cref="IsPaused"/>), as a game pauses, and its time sped up
/// or slowed down (<see cref="TimeScale"/>). Each frame tells what runs the world both (<see cref="Frame"/>).
/// </para>
/// <para>
/// The whole world can be saved as data (<see cref="SaveAsync"/>) and put back from it (<see cref="Restore"/>): the
/// domains open, every entity with its components and its scripts as they are. The engine does so across a restart
/// (<see cref="App.Restart"/>), so the world goes on as it was. Put back, it loads as a domain that replaces it
/// does, but with no loading domain shown, as it is there already: held out of sight from the frame it is put back
/// until its domains have been taken in again and every gem has readied it, then shown, drawn as it loaded.
/// </para>
/// </summary>
public sealed class World : IDisposable
{
    private readonly IEcs? _ecs;
    private readonly IScripts? _scripts;
    private readonly Assemblies _assemblies;
    private readonly Assets _assets;
    private readonly Threads _threads;
    private readonly App _app;

    /// <summary>
    /// The domains open in the world, and all that opening, closing and keeping them takes; null without a gem that
    /// provides the ECS, as no domain opens then.
    /// </summary>
    private readonly Domains? _domains;

    internal World(
        IEcs? ecs,
        IScripts? scripts,
        Assemblies assemblies,
        Assets assets,
        Threads threads,
        Events events,
        App app)
    {
        _ecs = ecs;
        _scripts = scripts;
        _assemblies = assemblies;
        _assets = assets;
        _threads = threads;
        _app = app;
        _domains = ecs is null ? null : new Domains(this, ecs, assets, threads, events, app);
    }

    /// <summary>
    /// The open domains, in the order they were opened, those still loading among them; never the loading domain.
    /// </summary>
    public IReadOnlyList<Handle<Domain>> Opened => _domains?.Opened ?? [];

    /// <summary>
    /// What streams into the world as it stands this frame: where it is in having something take its place, what its
    /// domains spawn and what of that is still taken in. The same all frame, worked out as it begins;
    /// <see cref="WorldStream.None"/> without a gem that provides the ECS.
    /// </summary>
    public WorldStream Stream => _domains?.Stream ?? WorldStream.None;

    /// <summary>
    /// Whether a domain is opening in place of the world (<see cref="DomainOpenMode.Replace"/>): from when the open
    /// domains close until the new one is shown, or is not coming, while the loading domain is shown, then its last
    /// image while the new one is readied; or a saved world is being put back in its place (<see cref="Restore"/>),
    /// until it is shown. False from the frame the world is first shown (<see cref="StreamState.Shown"/>).
    /// </summary>
    public bool IsLoading => Stream.State is StreamState.Loading or StreamState.Readying;

    /// <summary>
    /// Whether the world has loaded: a domain is open, every open one is there, spawned, and none is opening in place
    /// of the world nor a saved world being put back, so what replaced it is shown. False while a domain is still being
    /// read, however it was opened, which <see cref="IsLoading"/> only covers for one opening in place of the world.
    /// </summary>
    public bool IsLoaded => _domains?.IsLoaded ?? false;

    /// <summary>
    /// Whether the world is paused, from the next frame (<see cref="Frame.IsPaused"/>): its time stands still, so there
    /// are no fixed steps and physics holds, and of the scripts and systems only those whose class is
    /// <see cref="Attributes.UnpausableAttribute">[Unpausable]</see>, or a script on an entity tagged
    /// <see cref="Tag.Unpausable"/>, run; of the sounds, only such an entity's play. The gems go on, so the world is
    /// still drawn, and the window, the input and the debug UI work. While a domain loads in place of the world
    /// (<see cref="IsLoading"/>) it is not paused, as the gems ready the new one on the frames' fixed steps; it is again
    /// once that one is shown. Saved with the world. The app's own pause, which holds the frames, is
    /// <see cref="App.IsPaused"/>.
    /// </summary>
    public bool IsPaused { get; set; }

    /// <summary>
    /// How fast the world's time runs against real time, from the next frame: 1 as it is, 0.5 half as fast, 2 twice as
    /// fast, and below 0 as 0, which stands it still with every script still running. The fixed steps keep their length
    /// and come more or less often, up to <c>Core.maxFixedSteps</c> a frame, past which the world falls behind what
    /// was asked; sound plays at its own speed whatever this is. 1 while a domain loads in place of the world
    /// (<see cref="IsLoading"/>), as <see cref="IsPaused"/> says. Saved with the world.
    /// </summary>
    public float TimeScale { get; set => field = Math.Max(value, 0); } = 1;

    public void Dispose() => _domains?.Dispose();

    /// <summary>
    /// Opens <paramref name="domain"/> as <paramref name="mode"/> says, telling <paramref name="progress"/> how far it
    /// is, 0 to 1. The task ends once the domain is there, and shown when it replaces the world, with it; with why not
    /// when its file does not load, before anything else is closed, or when it is closed before then. A prefab of it
    /// that does not load is an error, and it opens without it. Opening one already open keeps it, and closes the
    /// others when it replaces them. Without a gem that provides the ECS, nothing opens. Any thread.
    /// </summary>
    public Task<Result<Domain>> OpenAsync(
        Handle<Domain> domain,
        DomainOpenMode mode,
        IProgress<float>? progress = null,
        CancellationToken cancel = default)
    {
        return _domains?.OpenAsync(domain, mode, progress, cancel)
            ?? Task.FromResult(
                Result.Failure<Domain>("The domain does not open: no gem the project lists provides the ECS."));
    }

    /// <summary>
    /// Closes <paramref name="domain"/>, destroying what it spawned; nothing happens when it is not open.
    /// </summary>
    public void Close(Handle<Domain> domain) => _domains?.Close(domain);

    /// <summary>
    /// The world as data (<see cref="WorldState"/>), to put back with <see cref="Restore"/>: the domains open, every
    /// entity with its components and its scripts (<see cref="IScripts.Save"/>), whether it is paused and how fast its
    /// time runs. It is taken on the main thread
    /// in one go, so it is the world as of one moment: now, on the main thread, else when the main thread next runs
    /// what it is handed; each entity's components copied out and its scripts' values read. The components are then
    /// written as JSON on a worker, which tells <paramref name="progress"/> how far it is, 0 to 1. The loading domain's
    /// entities are left out, as it is shown again as a domain opens anew. A component JSON cannot hold is an error,
    /// logged once per type, and left out. Any thread. Without a gem that provides the ECS, an empty one.
    /// </summary>
    public async Task<WorldState> SaveAsync(IProgress<float>? progress = null, CancellationToken cancel = default)
    {
        if (_ecs is null || _domains is null)
        {
            progress?.Report(1);

            return new WorldState { Domains = [], Entities = [] };
        }

        IEcs ecs = _ecs;
        Domains domains = _domains;
        Snapshot snapshot = _threads.IsCurrent(EngineThread.Main)
            ? TakeSnapshot(ecs, domains)
            : await _threads.PostAsync(EngineThread.Main, () => TakeSnapshot(ecs, domains), cancel)
                .ConfigureAwait(false);
        List<EntityState> entities = await Task.Run(() => WriteEntities(snapshot.Entities, progress, cancel), cancel)
            .ConfigureAwait(false);

        return new WorldState
        {
            Domains = snapshot.Domains,
            Entities = entities,
            IsPaused = snapshot.IsPaused,
            TimeScale = snapshot.TimeScale,
        };
    }

    /// <summary>
    /// Puts the world back as <paramref name="state"/> holds it (<see cref="SaveAsync"/>), in place of whatever is
    /// open: empties the world, closing what is open and destroying every entity, its globals too, as the saved world
    /// holds its own, while every gem forgets it (<see cref="WorldStream.IsEmptied"/>); then makes each saved entity
    /// with no components first, so that whichever is made earlier, a reference to one in a component or a script maps
    /// to it, then gives each its components and its scripts, put back as they were and not started
    /// (<see cref="IScripts.Restore"/>), and opens each domain again around the entities its prefabs had spawned, so
    /// closing one and a prefab read again still find them. The world loads meanwhile
    /// (<see cref="IsLoading"/>), held out of sight from this call as one that replaced it is while it is readied
    /// (<see cref="StreamState.Readying"/>), so nothing that runs it runs, its scripts put back included; no loading
    /// domain is shown, as what is put back is there already. Each domain's folder is read on workers after, as when it
    /// opens, so a prefab added since is spawned and one gone has its entities destroyed, and what its prefabs draw is
    /// taken in and held as it would be; once every domain has, every gem readies the world again, and it is shown once
    /// they all have, drawn as it loaded. No player is put, as the one saved is among the entities. A domain that had
    /// spawned nothing, as one still opening, opens again from the start. The world is paused, or not, and its time
    /// scaled as it was saved. A component or a script no code loaded declares, or whose saved values no longer fit
    /// it, is an error, and the entity goes without it. Main thread;
    /// without a gem that provides the ECS, nothing is restored, which is warned about.
    /// </summary>
    public void Restore(WorldState state)
    {
        if (_domains is null)
        {
            Debugging.Warn("The world is not restored: no gem the project lists provides the ECS.");

            return;
        }

        _domains.EmptyWorldForRestore();

        // Every saved entity is made empty first, so a component or a script read below that refers to another finds
        // it made, whichever comes first.
        Dictionary<int, Entity> made = new(state.Entities.Count);
        foreach (EntityState saved in state.Entities)
        {
            made[saved.Id] = GetEcs().Create();
        }

        // The domains before the components and the scripts, as putting them back holds the world out of sight: a
        // script put back then waits until the world is shown, as one started while a domain is readied does.
        _domains.Restore(state.Domains, made);
        RestoreEntities(state.Entities, made);

        IsPaused = state.IsPaused;
        TimeScale = state.TimeScale;
        Debugging.Info($"Restored the world: {state.Entities.Count} entities in {state.Domains.Count} domains.");
    }

    /// <summary>
    /// Puts <paramref name="asset"/> in the world at <paramref name="at"/>, as it says
    /// (<see cref="ISpawnable.Spawn"/>), and hands back the entities made: the caller's to destroy, until the world
    /// empties for a domain that replaces it.
    /// </summary>
    public Entity[] Spawn(ISpawnable asset, Transform at) => asset.Spawn(this, at);

    /// <summary>
    /// <see cref="Spawn"/> of <paramref name="asset"/>, loaded first; why not when it does not load. Any thread.
    /// </summary>
    public async Task<Result<Entity[]>> SpawnAsync<T>(Handle<T> asset, Transform at, CancellationToken cancel = default)
        where T : IAsset<T>, ISpawnable
    {
        Result<T> loaded = await _assets.LoadAsync(asset, cancel).ConfigureAwait(false);

        return loaded.Ok
            ? Result.Success(await _threads.PostAsync(EngineThread.Main, () => Spawn(loaded.Value, at), cancel)
                .ConfigureAwait(false))
            : Result.Failure<Entity[]>(loaded.Error);
    }

    /// <summary>
    /// Gives <paramref name="entity"/> its own copy of <paramref name="script"/>, with the values the prefab gives it
    /// (<see cref="IScripts.Attach"/>): its script joins the entity's <see cref="Scripts"/>, and the gem that runs
    /// scripts makes and starts the copy in the next frame, once whatever spawned with the entity is all there, and
    /// runs it until the entity is destroyed. Without a gem that runs scripts, the entity goes without it, which is
    /// warned about once.
    /// </summary>
    public void Attach(Entity entity, PrefabScript script) => _scripts?.Attach(entity, script);

    /// <inheritdoc cref="IEcs.Create()"/>
    public Entity Create() => GetEcs().Create();

    /// <inheritdoc cref="IEcs.Create(ReadOnlySpan{IComponent})"/>
    public Entity Create(ReadOnlySpan<IComponent> components) => GetEcs().Create(components);

    /// <inheritdoc cref="IEcs.Create{T1}(ReadOnlySpan{T1})"/>
    public ReadOnlySpan<Entity> Create<T1>(ReadOnlySpan<T1> first)
        where T1 : unmanaged, IComponent => GetEcs().Create(first);

    /// <inheritdoc cref="IEcs.Create{T1, T2}(ReadOnlySpan{T1}, ReadOnlySpan{T2})"/>
    public ReadOnlySpan<Entity> Create<T1, T2>(ReadOnlySpan<T1> first, ReadOnlySpan<T2> second)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent => GetEcs().Create(first, second);

    /// <summary>
    /// Makes an entity of <paramref name="components"/> that is the world's own rather than a domain's, a global:
    /// tagged <see cref="Tag.Global"/> beside the tags they give it, it outlives every domain that replaces the world,
    /// and only its maker, or a saved world put back in its place (<see cref="Restore"/>), destroys it. Throws when its
    /// tags have no room for the tag.
    /// </summary>
    public Entity CreateGlobal(ReadOnlySpan<IComponent> components)
    {
        Tags tags = default;
        foreach (IComponent component in components)
        {
            if (component is Tags given) tags = given;
        }

        if (!tags.Has(Tag.Global) && !tags.Add(Tag.Global))
        {
            throw new ArgumentException(
                $"The entity's tags have no room to tag it global: an entity has at most {Tags.Capacity}.",
                nameof(components));
        }

        Entity entity = GetEcs().Create(components);
        GetEcs().Set(entity, in tags);

        return entity;
    }

    /// <summary>
    /// The world's globals (<see cref="CreateGlobal"/>): every entity tagged <see cref="Tag.Global"/>.
    /// </summary>
    public IReadOnlyList<Entity> GetGlobals()
    {
        List<Entity> globals = [];
        foreach ((ReadOnlySpan<Entity> entities, Span<Tags> tags) in GetEcs().Query<Tags>())
        {
            for (int i = 0; i < entities.Length; i++)
            {
                if (tags[i].Has(Tag.Global)) globals.Add(entities[i]);
            }
        }

        return globals;
    }

    /// <inheritdoc cref="IEcs.Destroy"/>
    public void Destroy(Entity entity) => GetEcs().Destroy(entity);

    /// <inheritdoc cref="IEcs.IsAlive"/>
    public bool IsAlive(Entity entity) => GetEcs().IsAlive(entity);

    /// <inheritdoc cref="IEcs.Set(Entity, ReadOnlySpan{IComponent})"/>
    public void Set(Entity entity, ReadOnlySpan<IComponent> components) => GetEcs().Set(entity, components);

    /// <inheritdoc cref="IEcs.GetComponents"/>
    public IComponent[] GetComponents(Entity entity) => GetEcs().GetComponents(entity);

    /// <inheritdoc cref="IEcs.Set{T}"/>
    public void Set<T>(Entity entity, in T component)
        where T : unmanaged, IComponent => GetEcs().Set(entity, component);

    /// <inheritdoc cref="IEcs.Get{T}"/>
    public ref T Get<T>(Entity entity)
        where T : unmanaged, IComponent => ref GetEcs().Get<T>(entity);

    /// <inheritdoc cref="IEcs.Has{T}"/>
    public bool Has<T>(Entity entity)
        where T : unmanaged, IComponent => GetEcs().Has<T>(entity);

    /// <inheritdoc cref="IEcs.Remove{T}"/>
    public void Remove<T>(Entity entity)
        where T : unmanaged, IComponent => GetEcs().Remove<T>(entity);

    /// <inheritdoc cref="IEcs.Query()"/>
    public EcsQuery Query() => GetEcs().Query();

    /// <inheritdoc cref="IEcs.Query{T1}"/>
    public EcsQuery<T1> Query<T1>()
        where T1 : unmanaged, IComponent => GetEcs().Query<T1>();

    /// <inheritdoc cref="IEcs.Query{T1, T2}"/>
    public EcsQuery<T1, T2> Query<T1, T2>()
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent => GetEcs().Query<T1, T2>();

    /// <inheritdoc cref="IEcs.Query{T1, T2, T3}"/>
    public EcsQuery<T1, T2, T3> Query<T1, T2, T3>()
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent => GetEcs().Query<T1, T2, T3>();

    /// <inheritdoc cref="IEcs.Query{T1, T2, T3, T4}"/>
    public EcsQuery<T1, T2, T3, T4> Query<T1, T2, T3, T4>()
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent => GetEcs().Query<T1, T2, T3, T4>();

    /// <inheritdoc cref="IEcs.Register(ISystem)"/>
    public Subscription Register(ISystem system) => GetEcs().Register(system);

    /// <inheritdoc cref="IEcs.Register{T1}(ISystem{T1})"/>
    public Subscription Register<T1>(ISystem<T1> system)
        where T1 : unmanaged, IComponent => GetEcs().Register(system);

    /// <inheritdoc cref="IEcs.Register{T1, T2}(ISystem{T1, T2})"/>
    public Subscription Register<T1, T2>(ISystem<T1, T2> system)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent => GetEcs().Register(system);

    /// <inheritdoc cref="IEcs.Register{T1, T2, T3}(ISystem{T1, T2, T3})"/>
    public Subscription Register<T1, T2, T3>(ISystem<T1, T2, T3> system)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent => GetEcs().Register(system);

    /// <inheritdoc cref="IEcs.Register{T1, T2, T3, T4}(ISystem{T1, T2, T3, T4})"/>
    public Subscription Register<T1, T2, T3, T4>(ISystem<T1, T2, T3, T4> system)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent => GetEcs().Register(system);

    /// <inheritdoc cref="IEcs.Register(IParallelSystem)"/>
    public Subscription Register(IParallelSystem system) => GetEcs().Register(system);

    /// <inheritdoc cref="IEcs.Register{T1}(IParallelSystem{T1})"/>
    public Subscription Register<T1>(IParallelSystem<T1> system)
        where T1 : unmanaged, IComponent => GetEcs().Register(system);

    /// <inheritdoc cref="IEcs.Register{T1, T2}(IParallelSystem{T1, T2})"/>
    public Subscription Register<T1, T2>(IParallelSystem<T1, T2> system)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent => GetEcs().Register(system);

    /// <inheritdoc cref="IEcs.Register{T1, T2, T3}(IParallelSystem{T1, T2, T3})"/>
    public Subscription Register<T1, T2, T3>(IParallelSystem<T1, T2, T3> system)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent => GetEcs().Register(system);

    /// <inheritdoc cref="IEcs.Register{T1, T2, T3, T4}(IParallelSystem{T1, T2, T3, T4})"/>
    public Subscription Register<T1, T2, T3, T4>(IParallelSystem<T1, T2, T3, T4> system)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent => GetEcs().Register(system);

    /// <summary>
    /// Hands the domains the gems that take in what a domain spawns before it does, and ready one that replaces the
    /// world before it is shown (<see cref="IPreloader"/>). Whoever made the gems calls it once every one is, since the
    /// world is made while they are.
    /// </summary>
    internal void SetPreloaders(IReadOnlyList<IPreloader> preloaders) => _domains?.Preloaders = preloaders;

    /// <summary>
    /// Takes in what workers finished for the domains since the last frame, as frame <paramref name="frame"/> begins.
    /// Core calls it on the main thread at the start of each frame.
    /// </summary>
    internal void Pump(ulong frame) => _domains?.Pump(frame);

    /// <summary>
    /// Opens the project's entry point in place of the world; the default domain when it does not open. Core calls it
    /// on the first frame, once every gem is made. Without a gem that provides the ECS, nothing opens, which is warned
    /// about.
    /// </summary>
    internal async Task OpenEntryPointAsync()
    {
        if (_domains is null)
        {
            Debugging.Warn(
                "No gem the project lists provides the ECS, so the world holds no entities, and no domain opens.");

            return;
        }

        Result<Domain> opened = await OpenAsync(_app.Project.EntryPoint, DomainOpenMode.Replace, null, _app.Quitting)
            .ConfigureAwait(false);

        if (opened.Ok) return;

        Debugging.Error($"The entry point does not open, so the default domain does. {opened.Error}");
        Result<Domain> fallback = await OpenAsync(
            _assets.GetDefault<Domain>(),
            DomainOpenMode.Replace,
            null,
            _app.Quitting).ConfigureAwait(false);
        if (!fallback.Ok) Debugging.Error($"The default domain does not open either: {fallback.Error}");
    }

    /// <summary>
    /// Opens the project's entry point, then each other domain of the project's own but its loading domain, in place of
    /// the world, one after another, each once the last is shown, every gem having readied it, then stops the run:
    /// everything the domains show first is so made, and kept in the cache (--bake). Core calls it in place of
    /// <see cref="OpenEntryPointAsync"/>.
    /// </summary>
    internal async Task BakeAsync()
    {
        await OpenEntryPointAsync().ConfigureAwait(false);
        IReadOnlyList<Handle<Domain>> domains = _domains is null ? [] : _assets.GetProjectAssets<Domain>();
        foreach (Handle<Domain> domain in domains)
        {
            if (domain == _app.Project.EntryPoint || domain == _app.Project.LoadingDomain) continue;

            Result<Domain> opened = await OpenAsync(domain, DomainOpenMode.Replace, null, _app.Quitting)
                .ConfigureAwait(false);
            if (!opened.Ok) Debugging.Error($"{_assets.GetPath(domain)} does not open to be baked: {opened.Error}");
        }

        _app.Quit("every domain of the project is baked");
    }

    /// <summary>
    /// Each of <paramref name="entities"/> as the saved world holds it, its components written as JSON, telling
    /// <paramref name="progress"/> how far it is. Any thread: it touches nothing of the world.
    /// </summary>
    private static List<EntityState> WriteEntities(
        List<EntitySnapshot> entities,
        IProgress<float>? progress,
        CancellationToken cancel)
    {
        // Each entity's components as JSON; a type JSON cannot hold is logged once and left out of every entity.
        List<EntityState> written = new(entities.Count);
        HashSet<string> failed = new(StringComparer.Ordinal);
        foreach (EntitySnapshot entity in entities)
        {
            cancel.ThrowIfCancellationRequested();
            List<ComponentState> components = new(entity.Components.Length);
            foreach (IComponent component in entity.Components)
            {
                Type type = component.GetType();
                Result<JsonElement> values = Json.Write(component, type);
                if (values.Ok)
                {
                    components.Add(new ComponentState(type.Name, values.Value));
                }
                else if (failed.Add(type.Name))
                {
                    Debugging.Error($"{type.Name} components are not saved, as JSON cannot hold them, "
                        + $"so entities put back go without them: {values.Error}");
                }
            }

            written.Add(new EntityState(entity.Id, components, entity.Scripts));
            progress?.Report((float)written.Count / entities.Count);
        }

        progress?.Report(1);

        return written;
    }

    /// <summary>
    /// The world as it is now, as <see cref="SaveAsync"/> takes it: the domains, every entity but the loading domain's
    /// with its components copied out and its scripts' values read, and its pause and time scale. Main thread.
    /// </summary>
    private Snapshot TakeSnapshot(IEcs ecs, Domains domains)
    {
        List<EntitySnapshot> entities = [];
        HashSet<Entity> loading = domains.GetLoadingEntities();
        List<ScriptState> scripts = [];
        foreach (EcsChunk chunk in ecs.Query())
        {
            foreach (Entity entity in chunk.Entities)
            {
                if (loading.Contains(entity)) continue;

                _scripts?.Save(entity, scripts);
                entities.Add(
                    new EntitySnapshot(entity.Id, ecs.GetComponents(entity), scripts.Count == 0 ? [] : [.. scripts]));
                scripts.Clear();
            }
        }

        return new Snapshot(domains.Save(), entities, IsPaused, TimeScale);
    }

    /// <summary>
    /// Gives each saved entity of <paramref name="entities"/>, <paramref name="made"/> by its saved id, its components
    /// and its scripts, put back as they were and not started (<see cref="IScripts.Restore"/>). While they are read,
    /// an entity in their JSON reads as the one made for its saved id.
    /// </summary>
    private void RestoreEntities(IReadOnlyList<EntityState> entities, Dictionary<int, Entity> made)
    {
        EntityJsonConverter.Remap = made;
        try
        {
            // Each entity's components read back as the types they name; one no code loaded declares, or whose values
            // no longer fit it, is logged once and left out of every entity.
            HashSet<string> failed = new(StringComparer.Ordinal);
            List<IComponent> components = [];
            foreach (EntityState saved in entities)
            {
                components.Clear();
                foreach (ComponentState entry in saved.Components)
                {
                    if (ReadComponent(entry, failed) is { } component) components.Add(component);
                }

                GetEcs().Set(made[saved.Id], CollectionsMarshal.AsSpan(components));
            }

            foreach (EntityState saved in entities)
            {
                foreach (ScriptState script in saved.Scripts)
                {
                    _scripts?.Restore(made[saved.Id], script);
                }
            }
        }
        finally
        {
            EntityJsonConverter.Remap = null;
        }
    }

    /// <summary>
    /// A saved component read back as the type its name names (<see cref="Assemblies.TryGet{T}"/>); null, and an error
    /// logged once per name in <paramref name="failed"/>, when no code loaded declares it or its values no longer fit
    /// it.
    /// </summary>
    private IComponent? ReadComponent(ComponentState saved, HashSet<string> failed)
    {
        Result<Type> type = _assemblies.TryGet<IComponent>(saved.Type);
        Result<object> read = type.Ok ? Json.Read(saved.Values, type.Value) : Result.Failure<object>(type.Error);

        if (read.Ok) return (IComponent)read.Value;

        if (!failed.Add(saved.Type)) return null;

        Debugging.Error(
            $"Saved {saved.Type} components are not put back, so entities go without them: {read.Error}");

        return null;
    }

    /// <summary>
    /// The ECS a gem provides, which every entity, query and system call goes to; throws when no gem the project lists
    /// provides one, as the world then holds no entities.
    /// </summary>
    private IEcs GetEcs()
    {
        return _ecs ?? throw new InvalidOperationException(
            "No gem the project lists provides the ECS, so the world holds no entities.");
    }

    /// <summary>
    /// An entity as <see cref="SaveAsync"/> takes it from the world: its components, each a boxed copy, and its
    /// scripts' values, which is all it needs of the world, so the JSON can be written anywhere afterwards.
    /// </summary>
    private sealed record EntitySnapshot(int Id, IComponent[] Components, ScriptState[] Scripts);

    /// <summary>
    /// The world as <see cref="SaveAsync"/> takes it on the main thread, before its entities are written as JSON.
    /// </summary>
    private sealed record Snapshot(
        List<DomainState> Domains,
        List<EntitySnapshot> Entities,
        bool IsPaused,
        float TimeScale);
}
