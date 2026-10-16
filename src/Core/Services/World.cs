using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Magic.Attributes;
using Magic.Contexts.Assets;
using Magic.Contexts.ECS;
using Magic.Contexts.Subscriptions;
using Magic.Contexts.Threading;
using Magic.Contexts.Utils;
using Magic.Extensions;
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
/// (<see cref="Project.Loading"/>) until the new one is there; it then appears in the frame the loading domain closes,
/// with the project's player (<see cref="Project.Player"/>) at its spawn (<see cref="Tag.Spawn"/>). Opening one beside
/// them (<see cref="DomainOpenMode.Additive"/>) shows no loading domain and puts no player. Closing a domain destroys
/// what it spawned. While it is open, a prefab of it read again is spawned again as it is now, and prefabs added, moved
/// or removed in its folder are spawned or destroyed.
/// </para>
/// <para>
/// A script attached to an entity (<see cref="Attach"/>) is made and started once whatever is spawning it has all
/// spawned, so it never sees half a prefab or half a domain; while a domain loads in place of the world, once the domain
/// is there. One whose class is <see cref="ImpatientAttribute">[Impatient]</see> starts as it is attached.
/// </para>
/// <para>
/// The whole world can be saved as data (<see cref="Save"/>) and put back from it (<see cref="RestoreAsync"/>): the
/// domains open, every entity with its components and its scripts as they are. The engine does so across a restart
/// (<see cref="Lifetime.Restart"/>), so the world goes on as it was.
/// </para>
/// </summary>
public sealed class World : IDisposable
{
    /// <summary>
    /// The engine's loading domain, but its extension.
    /// </summary>
    private const string LoadingDomain = "Domains/Loading/Loading";

    private readonly IEcs? _ecs;
    private readonly IScripts? _scripts;
    private readonly Assets _assets;
    private readonly Threads _threads;
    private readonly Project _project;
    private readonly Lifetime _lifetime;
    private readonly SubscriptionList _watching = new();

    /// <summary>
    /// The open domains, in the order they were opened, those still loading among them; never the loading domain.
    /// </summary>
    private readonly List<Open> _domains = [];

    private readonly List<Handle<Domain>> _opened = [];

    /// <summary>
    /// The scripts attached while something spawns or a domain loads in place of the world, each with its entity, in
    /// the order they were attached.
    /// </summary>
    private readonly List<(Entity Entity, PrefabScript Script)> _held = [];

    /// <summary>
    /// The loading domain while it is shown.
    /// </summary>
    private Open? _loading;

    /// <summary>
    /// What <see cref="RestoreAsync"/> is putting back, until it has: what a save meanwhile hands on, as none of it is
    /// in the world yet. Written on workers and the main thread.
    /// </summary>
    private volatile WorldState? _restoring;

    /// <summary>
    /// Domains opening in place of the world and not there yet: the loading domain is shown while there is one.
    /// </summary>
    private int _replacing;

    /// <summary>
    /// How deep in spawns this is: a script attached inside one is held until the outermost is done.
    /// </summary>
    private int _spawning;

    private bool _isUnrunWarned;

    internal World(IEcs? ecs, IScripts? scripts, Assets assets, Threads threads, Events events, Project project, Lifetime lifetime)
    {
        _ecs = ecs;
        _scripts = scripts;
        _assets = assets;
        _threads = threads;
        _project = project;
        _lifetime = lifetime;
        _watching.Add(events.Watch<AssetReloaded<Domain>>(DomainReloaded));
        _watching.Add(events.Watch<AssetReloaded<Prefab>>(PrefabReloaded));
        _watching.Add(events.Watch<AssetsChanged>(FilesChanged));
    }

    public void Dispose()
    {
        _watching.Dispose();
    }

    /// <summary>
    /// The open domains, in the order they were opened, those still loading among them; never the loading domain.
    /// </summary>
    public IReadOnlyList<Handle<Domain>> Opened => _opened;

    /// <summary>
    /// Whether a domain is opening in place of the world (<see cref="DomainOpenMode.Replace"/>): from when the open
    /// domains close until the new one is there, or is not coming, while the loading domain is shown.
    /// </summary>
    public bool IsLoading => _replacing > 0;

    /// <summary>
    /// Opens <paramref name="domain"/> as <paramref name="mode"/> says, telling <paramref name="progress"/> how far it
    /// is, 0 to 1. The task ends once the domain is there, with it; with why not when its file does not load, before
    /// anything else is closed, or when it is closed before it is there. A prefab of it that does not load is an error,
    /// and it opens without it. Opening one already open keeps it, and closes the others when it replaces them. Without
    /// a gem that provides the ECS, nothing opens. Any thread.
    /// </summary>
    public async Task<Result<Domain>> OpenAsync(Handle<Domain> domain, DomainOpenMode mode, IProgress<float>? progress = null, CancellationToken cancel = default)
    {
        if (_ecs is null)
        {
            return Result.Failure<Domain>("The domain does not open: no gem the project lists provides the ECS.");
        }

        Result<Domain> file = await _assets.LoadAsync(domain, cancel).ConfigureAwait(false);
        if (!file.Ok)
        {
            return Result.Failure<Domain>($"The domain does not open: {file.Error}");
        }

        Open open = new(domain, _assets.Path(domain) ?? "A domain", file.Value, mode, isLoadingDomain: false);
        Open? already = await _threads.InvokeAsync(EngineThread.Main, () => Begin(open), cancel).ConfigureAwait(false);
        return already is not null ? Result.Success(already.Domain) : await ArriveAsync(open, progress, cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Closes <paramref name="domain"/>, destroying what it spawned; nothing happens when it is not open.
    /// </summary>
    public void Close(Handle<Domain> domain)
    {
        if (_domains.Find(open => open.Handle == domain) is { } found)
        {
            Close(found);
        }
    }

    /// <summary>
    /// Ends the game: the frames stop, and the gems end.
    /// </summary>
    public void End(string reason)
    {
        _lifetime.Stop(reason);
    }

    /// <summary>
    /// The world as data (<see cref="WorldState"/>), to put back with <see cref="RestoreAsync"/>: the domains open,
    /// every entity with its components and its scripts (<see cref="IScripts.Save"/>), and <paramref name="seconds"/>,
    /// the frame clock. The loading domain's entities are left out, as it is shown again as a domain opens anew. A
    /// component JSON cannot hold is an error, logged once per type, and left out. While a restore is still coming
    /// (<see cref="RestoreAsync"/>), what it will put back is what is saved, as none of it is in the world yet. Main
    /// thread. Without a gem that provides the ECS, an empty one.
    /// </summary>
    public WorldState Save(double seconds)
    {
        if (_restoring is { } pending)
        {
            return pending with { Seconds = seconds };
        }

        List<DomainState> domains = [];
        List<EntityState> entities = [];
        if (_ecs is null)
        {
            return new WorldState { Seconds = seconds, Domains = domains, Entities = entities };
        }

        foreach (Open open in _domains)
        {
            Dictionary<Handle<Prefab>, IReadOnlyList<int>> prefabs = [];
            foreach ((Handle<Prefab> file, Placed placed) in open.Prefabs)
            {
                if (placed.Entities is { } spawned)
                {
                    prefabs[file] = IdsOf(spawned);
                }
            }

            domains.Add(new DomainState(open.Handle, open.Mode, prefabs, IdsOf(open.Player ?? [])));
        }

        // The loading domain's entities are not the world's: it is shown again as a domain opens anew.
        HashSet<Entity> loading = [];
        if (_loading is { } shown)
        {
            foreach (Placed placed in shown.Prefabs.Values)
            {
                loading.UnionWith(placed.Entities ?? []);
            }
        }

        List<ScriptState> scripts = [];
        HashSet<string> failed = new(StringComparer.Ordinal);
        foreach (EcsChunk chunk in _ecs.Query())
        {
            foreach (Entity entity in chunk.Entities)
            {
                if (loading.Contains(entity))
                {
                    continue;
                }

                Dictionary<string, JsonElement> components = new(StringComparer.Ordinal);
                foreach (IComponent component in _ecs.Components(entity))
                {
                    Type type = component.GetType();
                    string name = Code.Component(type.Name) == type ? type.Name : type.FullName ?? type.Name;
                    try
                    {
                        components[name] = JsonSerializer.SerializeToElement(component, type, Json.Options);
                    }
                    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or JsonException)
                    {
                        if (failed.Add(name))
                        {
                            Debugging.Error($"{name} components are not saved, as JSON cannot hold them, so entities put back go without them: {ex.Message}");
                        }
                    }
                }

                _scripts?.Save(entity, scripts);
                entities.Add(new EntityState(entity.Id, components, scripts.Count == 0 ? [] : [.. scripts]));
                scripts.Clear();
            }
        }

        return new WorldState { Seconds = seconds, Domains = domains, Entities = entities };
    }

    /// <summary>
    /// Puts the world back as <paramref name="state"/> holds it (<see cref="Save"/>), in place of whatever is open,
    /// all in one frame: every entity, made anew with its components and its scripts put back
    /// (<see cref="IScripts.Restore"/>), not started, and each domain with the entities its prefabs spawned, so closing
    /// one and a prefab read again still find them. What a domain opens with is read on workers first, as when it
    /// opens, so a prefab added to its folder since is spawned and one gone has its entities destroyed, and the models
    /// its prefabs draw are held as they would be; no player is put, as the one saved is among the entities. A domain
    /// that had spawned nothing, as one still opening, opens again from the start. A component or a script no code
    /// loaded declares, or whose saved values no longer fit it, is an error, and the entity goes without it. Any thread;
    /// without a gem that provides the ECS, nothing is restored, which is warned about.
    /// </summary>
    public async Task RestoreAsync(WorldState state, CancellationToken cancel = default)
    {
        if (_ecs is null)
        {
            Debugging.Warn("The world is not restored: no gem the project lists provides the ECS.");
            return;
        }

        _restoring = state;
        List<(DomainState State, Open Open, Read? Read)> domains = [];
        foreach (DomainState saved in state.Domains)
        {
            Result<Domain> file = await _assets.LoadAsync(saved.Domain, cancel).ConfigureAwait(false);
            if (!file.Ok)
            {
                Debugging.Error($"{_assets.Path(saved.Domain) ?? "A domain"} is not restored: {file.Error}");
                continue;
            }

            Open open = new(saved.Domain, _assets.Path(saved.Domain) ?? "A domain", file.Value, saved.Mode, isLoadingDomain: false);
            bool isAnew = saved.Prefabs.Count == 0 && saved.Player.Count == 0;
            domains.Add((saved, open, isAnew ? null : await ReadAsync(open, null, cancel).ConfigureAwait(false)));
        }

        Dictionary<Handle<Script>, Script> scripts = [];
        foreach (EntityState entity in state.Entities)
        {
            foreach (ScriptState script in entity.Scripts)
            {
                if (scripts.ContainsKey(script.Script))
                {
                    continue;
                }

                Result<Script> loaded = await _assets.LoadAsync(script.Script, cancel).ConfigureAwait(false);
                if (loaded.Ok)
                {
                    scripts[script.Script] = loaded.Value;
                }
                else
                {
                    Debugging.Error($"Script {_assets.Path(script.Script) ?? script.Script.Id.ToString(CultureInfo.InvariantCulture)} is left off the entities it was on: {loaded.Error}");
                }
            }
        }

        await _threads.InvokeAsync(EngineThread.Main, () => Restore(state, domains, scripts), cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Puts <paramref name="asset"/> in the world at <paramref name="at"/>, as it says
    /// (<see cref="ISpawnable.Spawn"/>), and hands back the entities made: the caller's to destroy.
    /// </summary>
    public Entity[] Spawn(ISpawnable asset, Transform at)
    {
        _spawning++;
        try
        {
            return asset.Spawn(this, at);
        }
        finally
        {
            _spawning--;
            Release();
        }
    }

    /// <summary>
    /// <see cref="Spawn"/> of <paramref name="asset"/>, loaded first; why not when it does not load. Any thread.
    /// </summary>
    public async Task<Result<Entity[]>> SpawnAsync<T>(Handle<T> asset, Transform at, CancellationToken cancel = default)
        where T : IAsset<T>, ISpawnable
    {
        Result<T> loaded = await _assets.LoadAsync(asset, cancel).ConfigureAwait(false);
        return loaded.Ok
            ? Result.Success(await _threads.InvokeAsync(EngineThread.Main, () => Spawn(loaded.Value, at), cancel).ConfigureAwait(false))
            : Result.Failure<Entity[]>(loaded.Error);
    }

    /// <summary>
    /// Gives <paramref name="entity"/> its own copy of <paramref name="script"/>, made for it with the values the
    /// prefab gives it and run until the entity is destroyed (<see cref="IScripts.Attach"/>): made and started once
    /// whatever is spawning it has all spawned, and once the domain is there while one loads in place of the world,
    /// unless its class is <see cref="ImpatientAttribute">[Impatient]</see>. Without a gem that runs scripts, that is
    /// warned about once, and the entity goes without it.
    /// </summary>
    public void Attach(Entity entity, PrefabScript script)
    {
        if (_scripts is null)
        {
            if (!_isUnrunWarned)
            {
                _isUnrunWarned = true;
                Debugging.Warn($"An entity is given script {script.Script.Class.Name}, but no gem the project lists runs scripts; entities go without them.");
            }

            return;
        }

        if ((_spawning > 0 || IsLoading) && !script.Script.Class.IsDefined(typeof(ImpatientAttribute), inherit: false))
        {
            _held.Add((entity, script));
            return;
        }

        _scripts.Attach(entity, script);
    }

    /// <inheritdoc cref="IEcs.Create()"/>
    public Entity Create() => Ecs().Create();

    /// <inheritdoc cref="IEcs.Create(ReadOnlySpan{IComponent})"/>
    public Entity Create(ReadOnlySpan<IComponent> components) => Ecs().Create(components);

    /// <inheritdoc cref="IEcs.Create{T1}(ReadOnlySpan{T1})"/>
    public ReadOnlySpan<Entity> Create<T1>(ReadOnlySpan<T1> first)
        where T1 : unmanaged, IComponent => Ecs().Create(first);

    /// <inheritdoc cref="IEcs.Create{T1, T2}(ReadOnlySpan{T1}, ReadOnlySpan{T2})"/>
    public ReadOnlySpan<Entity> Create<T1, T2>(ReadOnlySpan<T1> first, ReadOnlySpan<T2> second)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent => Ecs().Create(first, second);

    /// <inheritdoc cref="IEcs.Destroy"/>
    public void Destroy(Entity entity) => Ecs().Destroy(entity);

    /// <inheritdoc cref="IEcs.IsAlive"/>
    public bool IsAlive(Entity entity) => Ecs().IsAlive(entity);

    /// <inheritdoc cref="IEcs.Set(Entity, ReadOnlySpan{IComponent})"/>
    public void Set(Entity entity, ReadOnlySpan<IComponent> components) => Ecs().Set(entity, components);

    /// <inheritdoc cref="IEcs.Components"/>
    public IComponent[] Components(Entity entity) => Ecs().Components(entity);

    /// <inheritdoc cref="IEcs.Set{T}"/>
    public void Set<T>(Entity entity, in T component)
        where T : unmanaged, IComponent => Ecs().Set(entity, component);

    /// <inheritdoc cref="IEcs.Get{T}"/>
    public ref T Get<T>(Entity entity)
        where T : unmanaged, IComponent => ref Ecs().Get<T>(entity);

    /// <inheritdoc cref="IEcs.Has{T}"/>
    public bool Has<T>(Entity entity)
        where T : unmanaged, IComponent => Ecs().Has<T>(entity);

    /// <inheritdoc cref="IEcs.Remove{T}"/>
    public void Remove<T>(Entity entity)
        where T : unmanaged, IComponent => Ecs().Remove<T>(entity);

    /// <inheritdoc cref="IEcs.Query()"/>
    public EcsQuery Query() => Ecs().Query();

    /// <inheritdoc cref="IEcs.Query{T1}"/>
    public EcsQuery<T1> Query<T1>()
        where T1 : unmanaged, IComponent => Ecs().Query<T1>();

    /// <inheritdoc cref="IEcs.Query{T1, T2}"/>
    public EcsQuery<T1, T2> Query<T1, T2>()
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent => Ecs().Query<T1, T2>();

    /// <inheritdoc cref="IEcs.Query{T1, T2, T3}"/>
    public EcsQuery<T1, T2, T3> Query<T1, T2, T3>()
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent => Ecs().Query<T1, T2, T3>();

    /// <inheritdoc cref="IEcs.Query{T1, T2, T3, T4}"/>
    public EcsQuery<T1, T2, T3, T4> Query<T1, T2, T3, T4>()
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent => Ecs().Query<T1, T2, T3, T4>();

    /// <inheritdoc cref="IEcs.Add(ISystem)"/>
    public Subscription Add(ISystem system) => Ecs().Add(system);

    /// <inheritdoc cref="IEcs.Add{T1}(ISystem{T1})"/>
    public Subscription Add<T1>(ISystem<T1> system)
        where T1 : unmanaged, IComponent => Ecs().Add(system);

    /// <inheritdoc cref="IEcs.Add{T1, T2}(ISystem{T1, T2})"/>
    public Subscription Add<T1, T2>(ISystem<T1, T2> system)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent => Ecs().Add(system);

    /// <inheritdoc cref="IEcs.Add{T1, T2, T3}(ISystem{T1, T2, T3})"/>
    public Subscription Add<T1, T2, T3>(ISystem<T1, T2, T3> system)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent => Ecs().Add(system);

    /// <inheritdoc cref="IEcs.Add{T1, T2, T3, T4}(ISystem{T1, T2, T3, T4})"/>
    public Subscription Add<T1, T2, T3, T4>(ISystem<T1, T2, T3, T4> system)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent => Ecs().Add(system);

    /// <inheritdoc cref="IEcs.Add(IParallelSystem)"/>
    public Subscription Add(IParallelSystem system) => Ecs().Add(system);

    /// <inheritdoc cref="IEcs.Add{T1}(IParallelSystem{T1})"/>
    public Subscription Add<T1>(IParallelSystem<T1> system)
        where T1 : unmanaged, IComponent => Ecs().Add(system);

    /// <inheritdoc cref="IEcs.Add{T1, T2}(IParallelSystem{T1, T2})"/>
    public Subscription Add<T1, T2>(IParallelSystem<T1, T2> system)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent => Ecs().Add(system);

    /// <inheritdoc cref="IEcs.Add{T1, T2, T3}(IParallelSystem{T1, T2, T3})"/>
    public Subscription Add<T1, T2, T3>(IParallelSystem<T1, T2, T3> system)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent => Ecs().Add(system);

    /// <inheritdoc cref="IEcs.Add{T1, T2, T3, T4}(IParallelSystem{T1, T2, T3, T4})"/>
    public Subscription Add<T1, T2, T3, T4>(IParallelSystem<T1, T2, T3, T4> system)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent => Ecs().Add(system);

    /// <summary>
    /// Opens the project's entry point in place of the world; the default domain when it does not open. Core calls it
    /// on the first frame, once every gem is made. Without a gem that provides the ECS, nothing opens, which is warned
    /// about.
    /// </summary>
    internal async Task OpenEntryPointAsync()
    {
        if (_ecs is null)
        {
            Debugging.Warn("No gem the project lists provides the ECS, so the world holds no entities, and no domain opens.");
            return;
        }

        Result<Domain> opened = await OpenAsync(_project.EntryPoint, DomainOpenMode.Replace, null, _lifetime.Stopping).ConfigureAwait(false);
        if (opened.Ok)
        {
            return;
        }

        Debugging.Error($"The entry point does not open, so the default domain does. {opened.Error}");
        Result<Domain> fallback = await OpenAsync(_assets.Default<Domain>(), DomainOpenMode.Replace, null, _lifetime.Stopping).ConfigureAwait(false);
        if (!fallback.Ok)
        {
            Debugging.Error($"The default domain does not open either: {fallback.Error}");
        }
    }

    /// <summary>
    /// The cell a prefab file named <c>x_y_z</c> is, as (x, y, z); null for any other name.
    /// </summary>
    private static Vector3? CellOf(string file)
    {
        string name = file;
        foreach (string extension in Prefab.Extensions)
        {
            if (file.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                name = file[..^extension.Length];
                break;
            }
        }

        string[] parts = name.Split('_');
        return parts.Length == 3
            && int.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int x)
            && int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int y)
            && int.TryParse(parts[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int z)
                ? new Vector3(x, y, z)
                : null;
    }

    /// <summary>
    /// The models <paramref name="parts"/> draw, and those of the parts under them, into <paramref name="models"/>.
    /// </summary>
    private static void AddDrawn(IReadOnlyList<PrefabPart> parts, HashSet<Handle<Model>> models)
    {
        foreach (PrefabPart part in parts)
        {
            foreach (IComponent component in part.Components)
            {
                if (component is Renderer { Model: var model } && model != Handle<Model>.None)
                {
                    models.Add(model);
                }
            }

            AddDrawn(part.Children, models);
        }
    }

    /// <summary>
    /// Starts opening <paramref name="open"/>, its file loaded: when it replaces the world, the open domains close and
    /// the loading domain is shown. The domain already open with its handle when there is one, which is kept.
    /// </summary>
    private Open? Begin(Open open)
    {
        Open? already = _domains.Find(other => other.Handle == open.Handle);
        if (open.Mode == DomainOpenMode.Replace)
        {
            for (int i = _domains.Count - 1; i >= 0; i--)
            {
                if (_domains[i] != already)
                {
                    Close(_domains[i]);
                }
            }
        }

        if (already is not null)
        {
            return already;
        }

        if (open.Mode == DomainOpenMode.Replace && _replacing++ == 0)
        {
            ShowLoading();
        }

        _domains.Add(open);
        _opened.Add(open.Handle);
        return null;
    }

    /// <summary>
    /// Reads what <paramref name="open"/> spawns, then spawns it on the main thread; closes it when reading is
    /// cancelled or throws, so a domain that is not coming never keeps the world loading.
    /// </summary>
    private async Task<Result<Domain>> ArriveAsync(Open open, IProgress<float>? progress, CancellationToken cancel)
    {
        Read read;
        try
        {
            read = await ReadAsync(open, progress, cancel).ConfigureAwait(false);
        }
        catch
        {
            _threads.Post(EngineThread.Main, () => Close(open));
            throw;
        }

        return await _threads.InvokeAsync(EngineThread.Main, () => Arrive(open, read), CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Every prefab in <paramref name="open"/>'s folder, each read on a worker, with the player when it replaces the
    /// world, and the models they draw loaded, so whatever draws them has them the frame they spawn. On a worker.
    /// </summary>
    private async Task<Read> ReadAsync(Open open, IProgress<float>? progress, CancellationToken cancel)
    {
        List<Placement> placements = PlacementsOf(open);
        int read = 0;
        Task<Result<Prefab>>[] reading = new Task<Result<Prefab>>[placements.Count];
        for (int i = 0; i < reading.Length; i++)
        {
            reading[i] = ReadOneAsync(placements[i].File);
        }

        Result<Prefab>[] prefabs = await Task.WhenAll(reading).ConfigureAwait(false);
        Prefab? player = null;
        if (open.Mode == DomainOpenMode.Replace && !open.IsLoadingDomain && _project.Player != Handle<Prefab>.None)
        {
            Result<Prefab> loaded = await _assets.LoadAsync(_project.Player, cancel).ConfigureAwait(false);
            if (loaded.Ok)
            {
                player = loaded.Value;
            }
            else
            {
                Debugging.Error($"The project's player is not spawned: {loaded.Error}");
            }
        }

        HashSet<Handle<Model>> drawn = [];
        foreach (Result<Prefab> prefab in prefabs)
        {
            if (prefab.Ok)
            {
                AddDrawn(prefab.Value.Parts, drawn);
            }
        }

        if (player is not null)
        {
            AddDrawn(player.Parts, drawn);
        }

        List<Task<Result<Model>>> loading = [];
        foreach (Handle<Model> model in drawn)
        {
            loading.Add(_assets.LoadAsync(model, cancel).AsTask());
        }

        List<Model> models = [];
        foreach (Result<Model> model in await Task.WhenAll(loading).ConfigureAwait(false))
        {
            if (model.Ok)
            {
                models.Add(model.Value);
            }
        }

        progress?.Report(1);
        return new Read(placements, prefabs, player, models);

        async Task<Result<Prefab>> ReadOneAsync(Handle<Prefab> file)
        {
            Result<Prefab> prefab = await _assets.LoadAsync(file, cancel).ConfigureAwait(false);
            progress?.Report(0.9f * Interlocked.Increment(ref read) / reading.Length);
            return prefab;
        }
    }

    /// <summary>
    /// Every prefab in <paramref name="open"/>'s folder, where it is spawned: a cell at its corner, any other at the
    /// origin. Any thread.
    /// </summary>
    private List<Placement> PlacementsOf(Open open)
    {
        string path = _assets.Path(open.Handle) ?? string.Empty;
        int slash = path.LastIndexOf('/');
        string folder = slash < 0 ? string.Empty : path[..slash];
        List<Placement> placements = [];
        foreach (string file in _assets.Paths(folder))
        {
            Handle<Prefab> prefab = _assets.Find<Prefab>(file);
            if (prefab == Handle<Prefab>.None)
            {
                continue;
            }

            Vector3? cell = CellOf(file[(slash + 1)..]);
            placements.Add(new Placement(prefab, cell is { } at ? at * open.Domain.CellSize : Vector3.Zero, cell.HasValue));
        }

        return placements;
    }

    /// <summary>
    /// What <paramref name="open"/> spawns is read: spawned now, all of it as one spawn, the player at its spawn,
    /// unless it was closed meanwhile; a domain that replaces the world takes the loading domain's place in this frame.
    /// </summary>
    private Result<Domain> Arrive(Open open, Read read)
    {
        if (open.IsClosed)
        {
            return Result.Failure<Domain>("It was closed before it was there.");
        }

        int entities = 0;
        _spawning++;
        try
        {
            for (int i = 0; i < read.Placements.Count; i++)
            {
                Placement placement = read.Placements[i];
                Placed placed = new(placement);
                open.Prefabs[placement.File] = placed;
                if (read.Prefabs[i] is { Ok: true, Value: { } prefab })
                {
                    Show(placed, prefab);
                    entities += placed.Entities?.Length ?? 0;
                }
                else
                {
                    Debugging.Error($"{_assets.Path(placement.File) ?? "A prefab"} of {open.Name} is left out: {read.Prefabs[i].Error}");
                }
            }

            open.Models = read.Models;
            if (read.Player is { } player)
            {
                open.Player = Spawn(player, SpawnOf(open));
            }
        }
        finally
        {
            _spawning--;
        }

        open.IsThere = true;
        if (!open.IsLoadingDomain)
        {
            Debugging.Info($"Opened {open.Name}: {entities} entities in {open.Prefabs.Count} prefabs.");
        }

        if (open.Mode == DomainOpenMode.Replace && !open.IsLoadingDomain)
        {
            Replaced();
        }

        Release();
        return Result.Success(open.Domain);
    }

    /// <summary>
    /// Where <paramref name="open"/>'s player is put: at its first entity tagged <see cref="Tag.Spawn"/>, in its
    /// prefabs that are not cells, then in its cells, turned as that entity is; else its origin, which is warned about.
    /// </summary>
    private static Transform SpawnOf(Open open)
    {
        foreach (bool cells in (ReadOnlySpan<bool>)[false, true])
        {
            foreach (Placed placed in open.Prefabs.Values)
            {
                if (placed.Placement.IsCell == cells && placed.Prefab?.PlaceOf(Tag.Spawn) is { } spawn)
                {
                    Transform at = spawn.Under(new Transform { Position = placed.Placement.Corner });
                    return new Transform { Position = at.Position, Rotation = at.Rotation };
                }
            }
        }

        Debugging.Warn($"{open.Name} has no entity tagged spawn, so the player is put at its origin.");
        return new Transform();
    }

    /// <summary>
    /// Spawns <paramref name="prefab"/> where <paramref name="placed"/> is, in place of what it spawned before.
    /// </summary>
    private void Show(Placed placed, Prefab prefab)
    {
        Destroy(placed.Entities);
        placed.Prefab = prefab;
        placed.Entities = Spawn(prefab, new Transform { Position = placed.Placement.Corner });
    }

    /// <summary>
    /// The ids of those of <paramref name="entities"/> that are alive, for a save.
    /// </summary>
    private int[] IdsOf(Entity[] entities)
    {
        List<int> ids = new(entities.Length);
        foreach (Entity entity in entities)
        {
            if (Ecs().IsAlive(entity))
            {
                ids.Add(entity.Id);
            }
        }

        return [.. ids];
    }

    /// <summary>
    /// The entities made for the saved <paramref name="ids"/>, those the save held.
    /// </summary>
    private static Entity[] EntitiesOf(IReadOnlyList<int> ids, Dictionary<int, Entity> made)
    {
        List<Entity> entities = new(ids.Count);
        foreach (int id in ids)
        {
            if (made.TryGetValue(id, out Entity entity))
            {
                entities.Add(entity);
            }
        }

        return [.. entities];
    }

    /// <summary>
    /// A saved component read back as the type <paramref name="name"/> names; null, and an error logged once per name
    /// in <paramref name="failed"/>, when no code loaded declares it or <paramref name="json"/> no longer fits it.
    /// </summary>
    private static IComponent? ComponentOf(string name, JsonElement json, HashSet<string> failed)
    {
        if (Code.Component(name) is not { } type)
        {
            if (failed.Add(name))
            {
                Debugging.Error($"Saved entities have a component {name}, which no code loaded declares; they go without it.");
            }

            return null;
        }

        try
        {
            return json.Deserialize(type, Json.Options) as IComponent;
        }
        catch (JsonException ex)
        {
            if (failed.Add(name))
            {
                Debugging.Error($"Saved {name} components no longer fit the type, so entities go without them: {ex.Message}");
            }

            return null;
        }
    }

    /// <summary>
    /// Puts the world back from <paramref name="state"/> (<see cref="RestoreAsync"/>), its domains' files and prefabs
    /// read: closes what is open, makes every entity empty first, so that whichever is made earlier, a reference to one
    /// in a component or a script maps to it, then gives each its components, places the domains, and puts the scripts
    /// back once every entity is whole. A domain read anew opens as <see cref="OpenAsync"/> opens one, in its turn.
    /// </summary>
    private void Restore(WorldState state, List<(DomainState State, Open Open, Read? Read)> domains, Dictionary<Handle<Script>, Script> scripts)
    {
        for (int i = _domains.Count - 1; i >= 0; i--)
        {
            Close(_domains[i]);
        }

        Dictionary<int, Entity> made = new(state.Entities.Count);
        foreach (EntityState saved in state.Entities)
        {
            made[saved.Id] = Ecs().Create();
        }

        EntityJsonConverter.Remap = made;
        _spawning++;
        try
        {
            HashSet<string> failed = new(StringComparer.Ordinal);
            List<IComponent> components = [];
            foreach (EntityState saved in state.Entities)
            {
                components.Clear();
                foreach ((string name, JsonElement json) in saved.Components)
                {
                    if (ComponentOf(name, json, failed) is { } component)
                    {
                        components.Add(component);
                    }
                }

                Ecs().Set(made[saved.Id], CollectionsMarshal.AsSpan(components));
            }

            foreach ((DomainState saved, Open open, Read? read) in domains)
            {
                if (read is null)
                {
                    if (Begin(open) is null)
                    {
                        ArriveAsync(open, null, _lifetime.Stopping).FireAndForget();
                    }
                }
                else
                {
                    Place(saved, open, read, made);
                }
            }

            foreach (EntityState saved in state.Entities)
            {
                foreach (ScriptState script in saved.Scripts)
                {
                    if (scripts.TryGetValue(script.Script, out Script? loaded))
                    {
                        _scripts?.Restore(made[saved.Id], loaded, script);
                    }
                }
            }
        }
        finally
        {
            EntityJsonConverter.Remap = null;
            _spawning--;
            if (_restoring == state)
            {
                _restoring = null;
            }
        }

        Release();
        Debugging.Info($"Restored the world: {state.Entities.Count} entities in {domains.Count} domains.");
    }

    /// <summary>
    /// Opens <paramref name="open"/> as the saved <paramref name="saved"/> had it, its prefabs <paramref name="read"/>:
    /// each prefab of its folder with the entities made for the ones it had spawned, or spawned now when it had none,
    /// as one added since; the entities of a prefab gone since are destroyed.
    /// </summary>
    private void Place(DomainState saved, Open open, Read read, Dictionary<int, Entity> made)
    {
        _domains.Add(open);
        _opened.Add(open.Handle);
        int entities = 0;
        for (int i = 0; i < read.Placements.Count; i++)
        {
            Placement placement = read.Placements[i];
            Placed placed = new(placement);
            open.Prefabs[placement.File] = placed;
            Result<Prefab> prefab = read.Prefabs[i];
            if (saved.Prefabs.TryGetValue(placement.File, out IReadOnlyList<int>? ids))
            {
                placed.Prefab = prefab.Ok ? prefab.Value : null;
                placed.Entities = EntitiesOf(ids, made);
            }
            else if (prefab.Ok)
            {
                Show(placed, prefab.Value);
            }
            else
            {
                Debugging.Error($"{_assets.Path(placement.File) ?? "A prefab"} of {open.Name} is left out: {prefab.Error}");
            }

            entities += placed.Entities?.Length ?? 0;
        }

        foreach ((Handle<Prefab> file, IReadOnlyList<int> ids) in saved.Prefabs)
        {
            if (!open.Prefabs.ContainsKey(file))
            {
                Destroy(EntitiesOf(ids, made));
            }
        }

        open.Models = read.Models;
        if (saved.Player.Count > 0)
        {
            open.Player = EntitiesOf(saved.Player, made);
        }

        open.IsThere = true;
        Debugging.Info($"Restored {open.Name}: {entities} entities in {open.Prefabs.Count} prefabs.");
    }

    /// <summary>
    /// Takes <paramref name="open"/> out of the world, destroying what it spawned and its player.
    /// </summary>
    private void Close(Open open)
    {
        if (open.IsClosed)
        {
            return;
        }

        open.IsClosed = true;
        if (open.IsLoadingDomain)
        {
            _loading = null;
        }
        else
        {
            _domains.Remove(open);
            _opened.Remove(open.Handle);
        }

        foreach (Placed placed in open.Prefabs.Values)
        {
            Destroy(placed.Entities);
        }

        open.Prefabs.Clear();
        open.Models = [];
        Destroy(open.Player);
        if (!open.IsThere && open.Mode == DomainOpenMode.Replace && !open.IsLoadingDomain)
        {
            Replaced();
        }
    }

    /// <summary>
    /// Shows the loading domain while a domain replaces the world, once its file has loaded: the project's, or the
    /// engine's.
    /// </summary>
    private void ShowLoading()
    {
        string path = LoadingDomain + Domain.Extensions[0];
        Handle<Domain> handle = _project.Loading == Handle<Domain>.None ? _assets.Find<Domain>(path) : _project.Loading;
        if (handle == Handle<Domain>.None)
        {
            Debugging.Warn($"There is no loading domain: the project names none, and there is no {path}.");
            return;
        }

        ShowLoadingAsync(handle).FireAndForget();
    }

    private async Task ShowLoadingAsync(Handle<Domain> handle)
    {
        Result<Domain> file = await _assets.LoadAsync(handle, _lifetime.Stopping).ConfigureAwait(false);
        if (!file.Ok)
        {
            Debugging.Error($"The loading domain does not open: {file.Error}");
            return;
        }

        Open loading = new(handle, _assets.Path(handle) ?? "The loading domain", file.Value, DomainOpenMode.Additive, isLoadingDomain: true);
        bool isShown = await _threads.InvokeAsync(EngineThread.Main, () =>
        {
            if (!IsLoading || _loading is not null)
            {
                return false;
            }

            _loading = loading;
            return true;
        }).ConfigureAwait(false);

        if (isShown)
        {
            await ArriveAsync(loading, null, _lifetime.Stopping).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A domain that was replacing the world is there, or is not coming: once none is left, the loading domain closes,
    /// in the frame the last appeared, and the scripts held while it loaded start.
    /// </summary>
    private void Replaced()
    {
        if (--_replacing > 0)
        {
            return;
        }

        if (_loading is { } loading)
        {
            Close(loading);
        }

        Release();
    }

    /// <summary>
    /// The ECS a gem provides, which every entity, query and system call goes to; throws when no gem the project lists
    /// provides one, as the world then holds no entities.
    /// </summary>
    private IEcs Ecs()
        => _ecs ?? throw new InvalidOperationException("No gem the project lists provides the ECS, so the world holds no entities.");

    /// <summary>
    /// Starts the scripts held, in the order they were attached, once nothing is spawning and no domain is loading in
    /// place of the world. One that a script attaches as it starts is started too.
    /// </summary>
    private void Release()
    {
        if (_spawning > 0 || IsLoading || _held.Count == 0 || _scripts is null)
        {
            return;
        }

        (Entity Entity, PrefabScript Script)[] held = [.. _held];
        _held.Clear();
        foreach ((Entity entity, PrefabScript script) in held)
        {
            _scripts.Attach(entity, script);
        }
    }

    private void Destroy(Entity[]? entities)
    {
        foreach (Entity entity in entities ?? [])
        {
            if (Ecs().IsAlive(entity))
            {
                Ecs().Destroy(entity);
            }
        }
    }

    /// <summary>
    /// The domains open, and the loading domain while it is shown.
    /// </summary>
    private List<Open> Shown()
    {
        List<Open> shown = [.. _domains];
        if (_loading is { } loading)
        {
            shown.Add(loading);
        }

        return shown;
    }

    /// <summary>
    /// A domain's file was read again: what it is now, which <see cref="FilesChanged"/>, told next, places its cells
    /// by.
    /// </summary>
    private void DomainReloaded(AssetReloaded<Domain> reloaded)
    {
        foreach (Open open in Shown())
        {
            if (open.Handle == reloaded.Asset.Handle)
            {
                open.Domain = reloaded.Asset;
            }
        }
    }

    /// <summary>
    /// A prefab was read again, as its file or a prefab it places changed: wherever a domain spawned it, it is spawned
    /// again as it is now. The player is not: what the player has done is the game's.
    /// </summary>
    private void PrefabReloaded(AssetReloaded<Prefab> reloaded)
    {
        foreach (Open open in Shown())
        {
            if (open.IsThere && open.Prefabs.TryGetValue(reloaded.Asset.Handle, out Placed? placed))
            {
                Show(placed, reloaded.Asset);
            }
        }
    }

    /// <summary>
    /// Files came, went, moved or changed: in each domain that is there, a prefab that came is read and spawned, one
    /// that went is destroyed, one that moved, or whose cell moved with the cell size, is spawned where it is now, and
    /// one that did not load is tried again.
    /// </summary>
    private void FilesChanged(AssetsChanged changed)
    {
        foreach (Open open in Shown())
        {
            if (!open.IsThere)
            {
                continue;
            }

            HashSet<Handle<Prefab>> present = [];
            foreach (Placement placement in PlacementsOf(open))
            {
                present.Add(placement.File);
                if (open.Prefabs.TryGetValue(placement.File, out Placed? placed) && placed.Placement == placement && placed.Entities is not null)
                {
                    continue;
                }

                if (placed is not null)
                {
                    Destroy(placed.Entities);
                }

                Placed added = new(placement);
                open.Prefabs[placement.File] = added;
                AddAsync(open, added).FireAndForget();
            }

            List<Handle<Prefab>> gone = [];
            foreach ((Handle<Prefab> file, Placed placed) in open.Prefabs)
            {
                if (!present.Contains(file))
                {
                    Destroy(placed.Entities);
                    gone.Add(file);
                }
            }

            foreach (Handle<Prefab> file in gone)
            {
                open.Prefabs.Remove(file);
            }
        }
    }

    /// <summary>
    /// Reads <paramref name="placed"/>'s prefab and spawns it, unless its domain closed, or another took its place, by
    /// then.
    /// </summary>
    private async Task AddAsync(Open open, Placed placed)
    {
        Result<Prefab> prefab = await _assets.LoadAsync(placed.Placement.File, _lifetime.Stopping).ConfigureAwait(false);
        _threads.Post(EngineThread.Main, () =>
        {
            if (open.IsClosed || !open.Prefabs.TryGetValue(placed.Placement.File, out Placed? now) || now != placed)
            {
                return;
            }

            if (prefab.Ok)
            {
                Show(placed, prefab.Value);
            }
            else
            {
                Debugging.Error($"{_assets.Path(placed.Placement.File) ?? "A prefab"} of {open.Name} is left out: {prefab.Error}");
            }
        });
    }

    /// <summary>
    /// A prefab of a domain, and where it is spawned: <see cref="Corner"/>, a cell's, or the domain's origin.
    /// </summary>
    private readonly record struct Placement(Handle<Prefab> File, Vector3 Corner, bool IsCell);

    /// <summary>
    /// What a domain's opening read: each of its prefabs, by placement, its player, and the models they draw.
    /// </summary>
    private sealed record Read(List<Placement> Placements, Result<Prefab>[] Prefabs, Prefab? Player, List<Model> Models);

    /// <summary>
    /// A domain open in the world, or opening.
    /// </summary>
    private sealed class Open(Handle<Domain> handle, string name, Domain domain, DomainOpenMode mode, bool isLoadingDomain)
    {
        public Handle<Domain> Handle { get; } = handle;

        /// <summary>
        /// Its path, for messages.
        /// </summary>
        public string Name { get; } = name;

        /// <summary>
        /// Its file, as it is now.
        /// </summary>
        public Domain Domain { get; set; } = domain;

        public DomainOpenMode Mode { get; } = mode;

        /// <summary>
        /// It is the loading domain, which is never among the domains open.
        /// </summary>
        public bool IsLoadingDomain { get; } = isLoadingDomain;

        /// <summary>
        /// Everything it spawns is spawned.
        /// </summary>
        public bool IsThere { get; set; }

        /// <summary>
        /// It was closed: what it read since is dropped.
        /// </summary>
        public bool IsClosed { get; set; }

        /// <summary>
        /// Its prefabs, by file.
        /// </summary>
        public Dictionary<Handle<Prefab>, Placed> Prefabs { get; } = [];

        /// <summary>
        /// The models its prefabs drew as it opened, held while it is open so whatever draws them keeps them.
        /// </summary>
        public List<Model> Models { get; set; } = [];

        /// <summary>
        /// The project's player, when it replaced the world.
        /// </summary>
        public Entity[]? Player { get; set; }
    }

    /// <summary>
    /// A prefab of a domain where it is spawned, and what it spawned: none yet, or when it did not load.
    /// </summary>
    private sealed class Placed(Placement placement)
    {
        public Placement Placement { get; } = placement;

        public Prefab? Prefab { get; set; }

        public Entity[]? Entities { get; set; }
    }
}
