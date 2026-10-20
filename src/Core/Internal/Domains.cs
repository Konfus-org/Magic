using System.Globalization;
using System.Numerics;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.ECS;
using Magic.Contexts.Subscriptions;
using Magic.Contexts.Threading;
using Magic.Contexts.Utils;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.Internal;

/// <summary>
/// The domains open in the world, and all that takes: opening one (<see cref="OpenAsync"/>), its prefabs read on
/// workers, taken in by the gems that need time to (<see cref="IPreloader"/>) and spawned on the main thread, the
/// loading domain shown while one replaces the world, closing one, each kept as its files change, and the domains'
/// part of a saved world (<see cref="Save"/>, <see cref="Restore"/>). The world makes one when a gem provides the ECS,
/// and is the one place to act on it: nothing else holds this; a gem below the world hears that one is loading on
/// <see cref="Events"/> (<see cref="WorldLoading"/>). Main thread, but where a member says otherwise.
/// </summary>
internal sealed class Domains : IDisposable
{
    /// <summary>
    /// The engine's loading domain, but its extension.
    /// </summary>
    private const string LoadingDomain = "Domains/Loading/Loading";

    private readonly World _world;
    private readonly IEcs _ecs;
    private readonly Assets _assets;
    private readonly Threads _threads;
    private readonly Project _project;
    private readonly Lifetime _lifetime;
    private readonly Events _events;
    private readonly SubscriptionList _watching = new();

    /// <summary>
    /// The open domains, in the order they were opened, those still loading among them; never the loading domain.
    /// </summary>
    private readonly List<Open> _domains = [];

    private readonly List<Handle<Domain>> _opened = [];

    /// <summary>
    /// The loading domain while it is shown.
    /// </summary>
    private Open? _loading;

    /// <summary>
    /// Domains opening in place of the world and not there yet: the loading domain is shown while there is one.
    /// </summary>
    private int _replacing;

    public Domains(World world, IEcs ecs, Assets assets, Threads threads, Events events, Project project, Lifetime lifetime)
    {
        _world = world;
        _ecs = ecs;
        _assets = assets;
        _threads = threads;
        _project = project;
        _lifetime = lifetime;
        _events = events;
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
    /// and it opens without it. Opening one already open keeps it, and closes the others when it replaces them.
    /// Any thread.
    /// </summary>
    public async Task<Result<Domain>> OpenAsync(Handle<Domain> domain, DomainOpenMode mode, IProgress<float>? progress = null, CancellationToken cancel = default)
    {
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
    /// Closes every open domain, destroying what each spawned, as a world put back closes what is open first.
    /// </summary>
    public void CloseAll()
    {
        for (int i = _domains.Count - 1; i >= 0; i--)
        {
            Close(_domains[i]);
        }
    }

    /// <summary>
    /// Each open domain as a saved world holds it (<see cref="World.SaveAsync"/>): which, how it was opened, and the
    /// entities each of its prefabs spawned and its player, those alive, by id. Never the loading domain.
    /// </summary>
    public List<DomainState> Save()
    {
        List<DomainState> domains = [];
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

        return domains;
    }

    /// <summary>
    /// The loading domain's entities, which a saved world leaves out: it is shown again as a domain opens anew.
    /// </summary>
    public HashSet<Entity> LoadingEntities()
    {
        HashSet<Entity> loading = [];
        if (_loading is { } shown)
        {
            foreach (Placed placed in shown.Prefabs.Values)
            {
                loading.UnionWith(placed.Entities ?? []);
            }
        }

        return loading;
    }

    /// <summary>
    /// Each of the saved domains <paramref name="saved"/> read for <see cref="Restore"/>: its file loaded, and the
    /// prefabs of its folder read on workers as when it opens, so a prefab added since spawns and one gone has its
    /// entities destroyed; one that had spawned nothing is not read, and opens again from the start. A domain whose
    /// file does not load is an error, and is left out. Any thread.
    /// </summary>
    public async Task<List<Restoring>> ReadAsync(IReadOnlyList<DomainState> saved, CancellationToken cancel)
    {
        List<Restoring> domains = [];
        foreach (DomainState state in saved)
        {
            Result<Domain> file = await _assets.LoadAsync(state.Domain, cancel).ConfigureAwait(false);
            if (!file.Ok)
            {
                Debugging.Error($"{_assets.Path(state.Domain) ?? "A domain"} is not restored: {file.Error}");
                continue;
            }

            Open open = new(state.Domain, _assets.Path(state.Domain) ?? "A domain", file.Value, state.Mode, isLoadingDomain: false);
            bool isAnew = state.Prefabs.Count == 0 && state.Player.Count == 0;
            domains.Add(new Restoring(state, open, isAnew ? null : await ReadAsync(open, null, cancel).ConfigureAwait(false)));
        }

        return domains;
    }

    /// <summary>
    /// Puts the saved domains <paramref name="domains"/> back, read first
    /// (<see cref="ReadAsync(IReadOnlyList{DomainState}, CancellationToken)"/>), once every entity of the saved world
    /// is made, <paramref name="made"/> by its saved id: each with the entities its prefabs had spawned, or opening
    /// anew as <see cref="OpenAsync"/> opens one, in its turn.
    /// </summary>
    public void Restore(List<Restoring> domains, Dictionary<int, Entity> made)
    {
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
            _events.Publish(new WorldLoading(true));
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
    /// world, and the models they draw loaded, then all of it taken in by the gems that need time to
    /// (<see cref="IPreloader"/>), so whatever draws or simulates them has them the frame they spawn. The loading
    /// domain is not taken in first, so it shows as soon as it can. The reads are the first half of the progress, the
    /// gems' taking in the second. On a worker.
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

        List<Prefab> spawning = [];
        foreach (Result<Prefab> prefab in prefabs)
        {
            if (prefab.Ok)
            {
                spawning.Add(prefab.Value);
            }
        }

        if (player is not null)
        {
            spawning.Add(player);
        }

        SubscriptionList holds = open.IsLoadingDomain ? new SubscriptionList() : await PreloadAsync(spawning, progress, cancel).ConfigureAwait(false);
        progress?.Report(1);
        return new Read(placements, prefabs, player, models, holds);

        async Task<Result<Prefab>> ReadOneAsync(Handle<Prefab> file)
        {
            Result<Prefab> prefab = await _assets.LoadAsync(file, cancel).ConfigureAwait(false);
            progress?.Report(0.5f * Interlocked.Increment(ref read) / reading.Length);
            return prefab;
        }
    }

    /// <summary>
    /// Every gem that needs time to take in what spawns (<see cref="World.Preloaders"/>) takes in
    /// <paramref name="spawning"/>, all at once, each telling its share of the second half of
    /// <paramref name="progress"/>; what they hand back, which keeps it taken in, is let go together when one of them
    /// fails or the open is cancelled. On a worker.
    /// </summary>
    private async Task<SubscriptionList> PreloadAsync(List<Prefab> spawning, IProgress<float>? progress, CancellationToken cancel)
    {
        IReadOnlyList<IPreloader> preloaders = _world.Preloaders;
        float[] shares = new float[preloaders.Count];
        Task<Subscription>[] preloading = new Task<Subscription>[preloaders.Count];
        for (int i = 0; i < preloaders.Count; i++)
        {
            preloading[i] = preloaders[i].PreloadAsync(spawning, new Share(shares, i, progress), cancel);
        }

        SubscriptionList holds = new();
        try
        {
            foreach (Subscription hold in await Task.WhenAll(preloading).ConfigureAwait(false))
            {
                holds.Add(hold);
            }
        }
        catch
        {
            foreach (Task<Subscription> preload in preloading)
            {
                if (preload.IsCompletedSuccessfully)
                {
                    (await preload.ConfigureAwait(false)).Dispose();
                }
            }

            throw;
        }

        return holds;
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
            read.Holds.Dispose();
            return Result.Failure<Domain>("It was closed before it was there.");
        }

        open.Holds = read.Holds;
        int entities = 0;
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
            open.Player = _world.Spawn(player, SpawnOf(open));
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
        placed.Entities = _world.Spawn(prefab, new Transform { Position = placed.Placement.Corner });
    }

    /// <summary>
    /// The ids of those of <paramref name="entities"/> that are alive, for a save.
    /// </summary>
    private int[] IdsOf(Entity[] entities)
    {
        List<int> ids = new(entities.Length);
        foreach (Entity entity in entities)
        {
            if (_ecs.IsAlive(entity))
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
    /// Opens <paramref name="open"/> as the saved <paramref name="saved"/> had it, its prefabs <paramref name="read"/>:
    /// each prefab of its folder with the entities made for the ones it had spawned, or spawned now when it had none,
    /// as one added since; the entities of a prefab gone since are destroyed.
    /// </summary>
    private void Place(DomainState saved, Open open, Read read, Dictionary<int, Entity> made)
    {
        _domains.Add(open);
        _opened.Add(open.Handle);
        open.Holds = read.Holds;
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
        open.Holds.Dispose();
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
    /// in the frame the last appeared, and the scripts held while it loaded start, told by <see cref="WorldLoading"/>.
    /// </summary>
    private void Replaced()
    {
        if (--_replacing > 0)
        {
            return;
        }

        _events.Publish(new WorldLoading(false));
        if (_loading is { } loading)
        {
            Close(loading);
        }
    }

    private void Destroy(Entity[]? entities)
    {
        foreach (Entity entity in entities ?? [])
        {
            if (_ecs.IsAlive(entity))
            {
                _ecs.Destroy(entity);
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
    internal readonly record struct Placement(Handle<Prefab> File, Vector3 Corner, bool IsCell);

    /// <summary>
    /// What a domain's opening read: each of its prefabs, by placement, its player, the models they draw, and what
    /// the gems that took them in handed back, which keeps that taken in (<see cref="IPreloader"/>).
    /// </summary>
    internal sealed record Read(List<Placement> Placements, Result<Prefab>[] Prefabs, Prefab? Player, List<Model> Models, SubscriptionList Holds);

    /// <summary>
    /// A domain open in the world, or opening.
    /// </summary>
    internal sealed class Open(Handle<Domain> handle, string name, Domain domain, DomainOpenMode mode, bool isLoadingDomain)
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
        /// What the gems that took in its prefabs before they spawned handed back, held while it is open so they keep
        /// that taken in (<see cref="IPreloader"/>).
        /// </summary>
        public SubscriptionList Holds { get; set; } = new();

        /// <summary>
        /// The project's player, when it replaced the world.
        /// </summary>
        public Entity[]? Player { get; set; }
    }

    /// <summary>
    /// A prefab of a domain where it is spawned, and what it spawned: none yet, or when it did not load.
    /// </summary>
    internal sealed class Placed(Placement placement)
    {
        public Placement Placement { get; } = placement;

        public Prefab? Prefab { get; set; }

        public Entity[]? Entities { get; set; }
    }

    /// <summary>
    /// A saved domain being put back (<see cref="Restore"/>): as it was saved, opening, and its prefabs read, or null
    /// for one that had spawned nothing, which opens again from the start.
    /// </summary>
    internal sealed record Restoring(DomainState Saved, Open Open, Read? Read);

    /// <summary>
    /// One gem's share of the second half of a domain's progress, the gems taking in what it spawns: each report sets
    /// its share and tells the domain's the mean of them all. Any thread.
    /// </summary>
    private sealed class Share(float[] shares, int index, IProgress<float>? whole) : IProgress<float>
    {
        public void Report(float value)
        {
            shares[index] = value;
            float sum = 0;
            foreach (float share in shares)
            {
                sum += share;
            }

            whole?.Report(0.5f + (0.5f * sum / shares.Length));
        }
    }
}
