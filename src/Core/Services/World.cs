using System.Runtime.InteropServices;
using System.Text.Json;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.ECS;
using Magic.Contexts.Subscriptions;
using Magic.Contexts.Threading;
using Magic.Contexts.Utils;
using Magic.Interfaces;
using Magic.Internal;

namespace Magic.Services;

/// <summary>
/// The game world: everything in it, and the one place to act on it. It opens and closes domains, puts assets in the
/// world (<see cref="Spawn"/>), makes, finds and changes entities, adds systems and ends the game, over the pieces
/// underneath, which stay separate: the ECS a gem provides (<see cref="IEcs"/>), the scripts a gem runs
/// (<see cref="IScripts"/>), and the assets. Core always makes it, and opens the project's entry point through it on
/// the first frame. Without a gem that provides the ECS it holds no entities: no domain opens, which is warned about,
/// and a call that needs entities throws, saying so; ending the game still works. Main thread, but where a member says
/// otherwise.
/// <para>
/// A domain is a folder holding a <c>.domain</c> file (<see cref="Domain"/>) beside the prefabs it spawns: every one in
/// the folder, read on workers with the models they draw, then all spawned in one frame, a cell, named <c>x_y_z</c>, at
/// its corner, <see cref="Domain.CellSize"/> metres a side, and any other at the domain's origin. Nothing is streamed:
/// whatever draws the world picks each thing's detail by its distance. Opening a domain in place of the others
/// (<see cref="DomainOpenMode.Replace"/>) closes them and shows the project's loading domain
/// (<see cref="Project.Loading"/>) until the new one is there, spawned with the project's player
/// (<see cref="Project.Player"/>) at its spawn (<see cref="Tag.Spawn"/>) in the frame the loading domain closes; the
/// window keeps the loading domain's last image while every gem that needs to readies the new one out of sight
/// (<see cref="IPreloader.ReadyAsync"/>), and it is shown once they all have, drawn as it loaded, before anything moves
/// it. Opening one beside them (<see cref="DomainOpenMode.Additive"/>) shows no loading domain and puts no player.
/// Closing a domain destroys what it spawned. While it is open, a prefab of it read again is spawned again as it is now,
/// and prefabs added, moved or removed in its folder are spawned or destroyed.
/// </para>
/// <para>
/// A script attached to an entity (<see cref="Attach"/>) joins its <see cref="Scripts"/>, and the gem that runs
/// scripts makes and starts it in the next frame, so it never sees half a prefab or half a domain. While a domain loads
/// in place of the world, it starts as the domain is readied, so what it spawns is readied with it, and runs from the
/// frame after the world is first shown, unless its class is <see cref="Attributes.ImpatientAttribute">[Impatient]</see>.
/// </para>
/// <para>
/// The whole world can be saved as data (<see cref="SaveAsync"/>) and put back from it (<see cref="Restore"/>): the
/// domains open, every entity with its components and its scripts as they are. The engine does so across a restart
/// (<see cref="Lifetime.Restart"/>), so the world goes on as it was. Put back, it loads as a domain that replaces it
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
    private readonly Project _project;
    private readonly Lifetime _lifetime;

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
        Project project,
        Lifetime lifetime)
    {
        _ecs = ecs;
        _scripts = scripts;
        _assemblies = assemblies;
        _assets = assets;
        _threads = threads;
        _project = project;
        _lifetime = lifetime;
        _domains = ecs is null ? null : new Domains(this, ecs, assets, threads, events, project, lifetime);
    }

    /// <summary>
    /// The open domains, in the order they were opened, those still loading among them; never the loading domain.
    /// </summary>
    public IReadOnlyList<Handle<Domain>> Opened => _domains?.Opened ?? [];

    /// <summary>
    /// Whether a domain is opening in place of the world (<see cref="DomainOpenMode.Replace"/>): from when the open
    /// domains close until the new one is shown, or is not coming, while the loading domain is shown, then its last
    /// image while the new one is readied; or a saved world is being put back in its place (<see cref="Restore"/>),
    /// from the call until it is shown. False from the frame the world is first shown, which whatever draws it reads as
    /// it copies the frame; what runs the world hears it the frame after (<see cref="WorldLoading"/>).
    /// </summary>
    public bool IsLoading => _domains?.IsLoading ?? false;

    /// <summary>
    /// Whether the world has loaded: a domain is open, every open one is there, spawned, and none is opening in place
    /// of the world nor a saved world being put back, so what replaced it is shown. False while a domain is still being
    /// read, however it was opened, which <see cref="IsLoading"/> only covers for one opening in place of the world.
    /// </summary>
    public bool IsLoaded => _domains?.IsLoaded ?? false;

    /// <summary>
    /// The gems that take in what a domain spawns before it does, and ready one that replaces the world before it is
    /// shown (<see cref="IPreloader"/>), set by whoever made the gems once every one is, since the world is made while
    /// they are; none until then.
    /// </summary>
    internal IReadOnlyList<IPreloader> Preloaders { get; set; } = [];

    public void Dispose()
    {
        _domains?.Dispose();
    }

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
    public void Close(Handle<Domain> domain)
    {
        _domains?.Close(domain);
    }

    /// <summary>
    /// Ends the game: the frames stop, and the gems end.
    /// </summary>
    public void End(string reason)
    {
        _lifetime.Stop(reason);
    }

    /// <summary>
    /// The world as data (<see cref="WorldState"/>), to put back with <see cref="Restore"/>: the domains open, and
    /// every entity with its components and its scripts (<see cref="IScripts.Save"/>). It is taken on the main thread
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
        (List<DomainState> Domains, List<Saving> Entities) taken = _threads.IsCurrent(EngineThread.Main)
            ? Take(ecs, domains)
            : await _threads.InvokeAsync(EngineThread.Main, () => Take(ecs, domains), cancel).ConfigureAwait(false);
        List<EntityState> entities = await Task.Run(() => Write(taken.Entities, progress, cancel), cancel)
            .ConfigureAwait(false);

        return new WorldState { Domains = taken.Domains, Entities = entities };
    }

    /// <summary>
    /// Puts the world back as <paramref name="state"/> holds it (<see cref="SaveAsync"/>), in place of whatever is
    /// open: closes what is open, makes every entity empty first, so that whichever is made earlier, a reference to one
    /// in a component or a script maps to it, then gives each its components and its scripts, put back as they were and
    /// not started (<see cref="IScripts.Restore"/>), and opens each domain again around the entities its prefabs had
    /// spawned, so closing one and a prefab read again still find them. The world loads meanwhile
    /// (<see cref="IsLoading"/>), held out of sight from this call as one that replaced it is while it is readied
    /// (<see cref="IPreloader.ReadyAsync"/>), so nothing that runs it runs, its scripts put back included; no loading
    /// domain is shown, as what is put back is there already. Each domain's folder is read on workers after, as when it
    /// opens, so a prefab added since is spawned and one gone has its entities destroyed, and what its prefabs draw is
    /// taken in and held as it would be; once every domain has, every gem readies the world again, and it is shown once
    /// they all have, drawn as it loaded. No player is put, as the one saved is among the entities. A domain that had
    /// spawned nothing, as one still opening, opens again from the start. A component or a script no code loaded
    /// declares, or whose saved values no longer fit it, is an error, and the entity goes without it. Main thread;
    /// without a gem that provides the ECS, nothing is restored, which is warned about.
    /// </summary>
    public void Restore(WorldState state)
    {
        if (_domains is null)
        {
            Debugging.Warn("The world is not restored: no gem the project lists provides the ECS.");
            return;
        }

        _domains.CloseAll();
        Dictionary<int, Entity> made = new(state.Entities.Count);
        foreach (EntityState saved in state.Entities)
        {
            made[saved.Id] = GetEcs().Create();
        }

        // The domains before the components and the scripts, as putting them back holds the world out of sight: a
        // script put back then waits until the world is shown, as one started while a domain is readied does.
        _domains.Restore(state.Domains, made);
        EntityJsonConverter.Remap = made;
        try
        {
            HashSet<string> failed = new(StringComparer.Ordinal);
            List<IComponent> components = [];
            foreach (EntityState saved in state.Entities)
            {
                components.Clear();
                foreach (ComponentState entry in saved.Components)
                {
                    if (ReadComponent(entry, failed) is { } component) components.Add(component);
                }

                GetEcs().Set(made[saved.Id], CollectionsMarshal.AsSpan(components));
            }

            foreach (EntityState saved in state.Entities)
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

        Debugging.Info($"Restored the world: {state.Entities.Count} entities in {state.Domains.Count} domains.");
    }

    /// <summary>
    /// Puts <paramref name="asset"/> in the world at <paramref name="at"/>, as it says
    /// (<see cref="ISpawnable.Spawn"/>), and hands back the entities made: the caller's to destroy.
    /// </summary>
    public Entity[] Spawn(ISpawnable asset, Transform at)
    {
        return asset.Spawn(this, at);
    }

    /// <summary>
    /// <see cref="Spawn"/> of <paramref name="asset"/>, loaded first; why not when it does not load. Any thread.
    /// </summary>
    public async Task<Result<Entity[]>> SpawnAsync<T>(Handle<T> asset, Transform at, CancellationToken cancel = default)
        where T : IAsset<T>, ISpawnable
    {
        Result<T> loaded = await _assets.LoadAsync(asset, cancel).ConfigureAwait(false);
        return loaded.Ok
            ? Result.Success(await _threads.InvokeAsync(EngineThread.Main, () => Spawn(loaded.Value, at), cancel)
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
    public void Attach(Entity entity, PrefabScript script)
    {
        _scripts?.Attach(entity, script);
    }

    /// <inheritdoc cref="IEcs.Create()"/>
    public Entity Create()
    {
        return GetEcs().Create();
    }

    /// <inheritdoc cref="IEcs.Create(ReadOnlySpan{IComponent})"/>
    public Entity Create(ReadOnlySpan<IComponent> components)
    {
        return GetEcs().Create(components);
    }

    /// <inheritdoc cref="IEcs.Create{T1}(ReadOnlySpan{T1})"/>
    public ReadOnlySpan<Entity> Create<T1>(ReadOnlySpan<T1> first)
        where T1 : unmanaged, IComponent
    {
        return GetEcs().Create(first);
    }

    /// <inheritdoc cref="IEcs.Create{T1, T2}(ReadOnlySpan{T1}, ReadOnlySpan{T2})"/>
    public ReadOnlySpan<Entity> Create<T1, T2>(ReadOnlySpan<T1> first, ReadOnlySpan<T2> second)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
    {
        return GetEcs().Create(first, second);
    }

    /// <inheritdoc cref="IEcs.Destroy"/>
    public void Destroy(Entity entity)
    {
        GetEcs().Destroy(entity);
    }

    /// <inheritdoc cref="IEcs.IsAlive"/>
    public bool IsAlive(Entity entity)
    {
        return GetEcs().IsAlive(entity);
    }

    /// <inheritdoc cref="IEcs.Set(Entity, ReadOnlySpan{IComponent})"/>
    public void Set(Entity entity, ReadOnlySpan<IComponent> components)
    {
        GetEcs().Set(entity, components);
    }

    /// <inheritdoc cref="IEcs.GetComponents"/>
    public IComponent[] GetComponents(Entity entity)
    {
        return GetEcs().GetComponents(entity);
    }

    /// <inheritdoc cref="IEcs.Set{T}"/>
    public void Set<T>(Entity entity, in T component)
        where T : unmanaged, IComponent
    {
        GetEcs().Set(entity, component);
    }

    /// <inheritdoc cref="IEcs.Get{T}"/>
    public ref T Get<T>(Entity entity)
        where T : unmanaged, IComponent
    {
        return ref GetEcs().Get<T>(entity);
    }

    /// <inheritdoc cref="IEcs.Has{T}"/>
    public bool Has<T>(Entity entity)
        where T : unmanaged, IComponent
    {
        return GetEcs().Has<T>(entity);
    }

    /// <inheritdoc cref="IEcs.Remove{T}"/>
    public void Remove<T>(Entity entity)
        where T : unmanaged, IComponent
    {
        GetEcs().Remove<T>(entity);
    }

    /// <inheritdoc cref="IEcs.Query()"/>
    public EcsQuery Query()
    {
        return GetEcs().Query();
    }

    /// <inheritdoc cref="IEcs.Query{T1}"/>
    public EcsQuery<T1> Query<T1>()
        where T1 : unmanaged, IComponent
    {
        return GetEcs().Query<T1>();
    }

    /// <inheritdoc cref="IEcs.Query{T1, T2}"/>
    public EcsQuery<T1, T2> Query<T1, T2>()
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
    {
        return GetEcs().Query<T1, T2>();
    }

    /// <inheritdoc cref="IEcs.Query{T1, T2, T3}"/>
    public EcsQuery<T1, T2, T3> Query<T1, T2, T3>()
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
    {
        return GetEcs().Query<T1, T2, T3>();
    }

    /// <inheritdoc cref="IEcs.Query{T1, T2, T3, T4}"/>
    public EcsQuery<T1, T2, T3, T4> Query<T1, T2, T3, T4>()
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent
    {
        return GetEcs().Query<T1, T2, T3, T4>();
    }

    /// <inheritdoc cref="IEcs.Add(ISystem)"/>
    public Subscription Add(ISystem system)
    {
        return GetEcs().Add(system);
    }

    /// <inheritdoc cref="IEcs.Add{T1}(ISystem{T1})"/>
    public Subscription Add<T1>(ISystem<T1> system)
        where T1 : unmanaged, IComponent
    {
        return GetEcs().Add(system);
    }

    /// <inheritdoc cref="IEcs.Add{T1, T2}(ISystem{T1, T2})"/>
    public Subscription Add<T1, T2>(ISystem<T1, T2> system)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
    {
        return GetEcs().Add(system);
    }

    /// <inheritdoc cref="IEcs.Add{T1, T2, T3}(ISystem{T1, T2, T3})"/>
    public Subscription Add<T1, T2, T3>(ISystem<T1, T2, T3> system)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
    {
        return GetEcs().Add(system);
    }

    /// <inheritdoc cref="IEcs.Add{T1, T2, T3, T4}(ISystem{T1, T2, T3, T4})"/>
    public Subscription Add<T1, T2, T3, T4>(ISystem<T1, T2, T3, T4> system)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent
    {
        return GetEcs().Add(system);
    }

    /// <inheritdoc cref="IEcs.Add(IParallelSystem)"/>
    public Subscription Add(IParallelSystem system)
    {
        return GetEcs().Add(system);
    }

    /// <inheritdoc cref="IEcs.Add{T1}(IParallelSystem{T1})"/>
    public Subscription Add<T1>(IParallelSystem<T1> system)
        where T1 : unmanaged, IComponent
    {
        return GetEcs().Add(system);
    }

    /// <inheritdoc cref="IEcs.Add{T1, T2}(IParallelSystem{T1, T2})"/>
    public Subscription Add<T1, T2>(IParallelSystem<T1, T2> system)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
    {
        return GetEcs().Add(system);
    }

    /// <inheritdoc cref="IEcs.Add{T1, T2, T3}(IParallelSystem{T1, T2, T3})"/>
    public Subscription Add<T1, T2, T3>(IParallelSystem<T1, T2, T3> system)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
    {
        return GetEcs().Add(system);
    }

    /// <inheritdoc cref="IEcs.Add{T1, T2, T3, T4}(IParallelSystem{T1, T2, T3, T4})"/>
    public Subscription Add<T1, T2, T3, T4>(IParallelSystem<T1, T2, T3, T4> system)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent
    {
        return GetEcs().Add(system);
    }

    /// <summary>
    /// Takes in what workers finished for the domains since the last frame. Core calls it on the main thread at the
    /// start of each frame.
    /// </summary>
    internal void TakeArrived()
    {
        _domains?.TakeArrived();
    }

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

        Result<Domain> opened = await OpenAsync(_project.EntryPoint, DomainOpenMode.Replace, null, _lifetime.Stopping)
            .ConfigureAwait(false);
        if (opened.Ok) return;

        Debugging.Error($"The entry point does not open, so the default domain does. {opened.Error}");
        Result<Domain> fallback = await OpenAsync(
            _assets.Default<Domain>(),
            DomainOpenMode.Replace,
            null,
            _lifetime.Stopping).ConfigureAwait(false);
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
            if (domain == _project.EntryPoint || domain == _project.Loading) continue;

            Result<Domain> opened = await OpenAsync(domain, DomainOpenMode.Replace, null, _lifetime.Stopping)
                .ConfigureAwait(false);
            if (!opened.Ok) Debugging.Error($"{_assets.GetPath(domain)} does not open to be baked: {opened.Error}");
        }

        _lifetime.Stop("every domain of the project is baked");
    }

    /// <summary>
    /// Each of <paramref name="entities"/> as the saved world holds it, its components written as JSON, telling
    /// <paramref name="progress"/> how far it is. Any thread: it touches nothing of the world.
    /// </summary>
    private static List<EntityState> Write(List<Saving> entities, IProgress<float>? progress, CancellationToken cancel)
    {
        List<EntityState> written = new(entities.Count);
        HashSet<string> failed = new(StringComparer.Ordinal);
        foreach (Saving entity in entities)
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
    /// The world as it is now, as <see cref="SaveAsync"/> takes it: the domains, and every entity but the loading
    /// domain's with its components copied out and its scripts' values read. Main thread.
    /// </summary>
    private (List<DomainState> Domains, List<Saving> Entities) Take(IEcs ecs, Domains domains)
    {
        List<Saving> entities = [];
        HashSet<Entity> loading = domains.GetLoadingEntities();
        List<ScriptState> scripts = [];
        foreach (EcsChunk chunk in ecs.Query())
        {
            foreach (Entity entity in chunk.Entities)
            {
                if (loading.Contains(entity)) continue;

                _scripts?.Save(entity, scripts);
                entities.Add(new Saving(entity.Id, ecs.GetComponents(entity), scripts.Count == 0 ? [] : [.. scripts]));
                scripts.Clear();
            }
        }

        return (domains.Save(), entities);
    }

    /// <summary>
    /// A saved component read back as the type its name names (<see cref="Assemblies.Get{T}"/>); null, and an error
    /// logged once per name in <paramref name="failed"/>, when no code loaded declares it or its values no longer fit
    /// it.
    /// </summary>
    private IComponent? ReadComponent(ComponentState saved, HashSet<string> failed)
    {
        Result<Type> type = _assemblies.Get<IComponent>(saved.Type);
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
    private sealed record Saving(int Id, IComponent[] Components, ScriptState[] Scripts);
}
