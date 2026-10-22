using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Channels;
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
/// loading domain, taken in once and kept, shown from the frame one begins to replace the world, and the one replacing
/// it readied by those gems out of sight before it is shown, closing one, each kept as its files change, and the
/// domains' part of a saved world (<see cref="Save"/>, <see cref="Restore"/>), readied out of sight too as it is put
/// back. The world makes one when a gem provides the ECS, and is the one place to act on it: nothing else holds this; a
/// gem below the world hears that one is loading on <see cref="Events"/> (<see cref="WorldLoading"/>). Main thread, but
/// where a member says otherwise.
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

    /// <summary>
    /// What workers finished for the main thread to take in at the start of a frame (<see cref="TakeArrived"/>): a
    /// prefab read for a placement, a domain whose reading failed, which closes, or one every gem readied, which is
    /// shown, as is a world put back every gem readied.
    /// </summary>
    private readonly Channel<Arrived> _arrived = Channel.CreateUnbounded<Arrived>(
        new UnboundedChannelOptions { SingleReader = true });

    /// <summary>
    /// The loading domain while it is shown.
    /// </summary>
    private Open? _loading;

    /// <summary>
    /// The loading domain, read and taken in by every gem that needs time to the first time a domain replaced the world,
    /// then kept so, so it is shown whole from the frame the open domains close (<see cref="ShowLoading"/>); none
    /// before, or once its files changed (<see cref="ForgetLoading"/>).
    /// </summary>
    private Keeping? _keeping;

    /// <summary>
    /// Domains opening in place of the world and saved worlds put back in its place (<see cref="Restore"/>), not shown
    /// yet: the world is loading while there is one.
    /// </summary>
    private int _replacing;

    /// <summary>
    /// The world was shown, or the domain replacing it is not coming, since the last frame began: told as the next one
    /// begins (<see cref="TakeArrived"/>), so the frame that first shows the world draws it as it loaded, before anything
    /// that hears it runs.
    /// </summary>
    private bool _isTelling;

    internal Domains(
        World world,
        IEcs ecs,
        Assets assets,
        Threads threads,
        Events events,
        Project project,
        Lifetime lifetime)
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

    /// <summary>
    /// The open domains, in the order they were opened, those still loading among them; never the loading domain.
    /// </summary>
    internal IReadOnlyList<Handle<Domain>> Opened
    {
        get
        {
            List<Handle<Domain>> opened = new(_domains.Count);
            foreach (Open open in _domains)
            {
                opened.Add(open.Handle);
            }

            return opened;
        }
    }

    /// <summary>
    /// Whether a domain is opening in place of the world (<see cref="DomainOpenMode.Replace"/>): from when the open
    /// domains close until the new one is shown, or is not coming, while the loading domain is shown, then its last
    /// image while the new one is readied; or a saved world is put back in its place (<see cref="Restore"/>), until it is
    /// shown.
    /// </summary>
    internal bool IsLoading => _replacing > 0;

    /// <summary>
    /// Whether the world has loaded: a domain other than the loading domain is open, every open one is there, spawned,
    /// and none is opening in place of the world, nor a saved world being put back, so what replaced it is shown.
    /// </summary>
    internal bool IsLoaded => _replacing == 0
        && _domains.Exists(static open => !open.IsLoadingDomain)
        && _domains.TrueForAll(static open => open.IsThere);

    public void Dispose()
    {
        _watching.Dispose();
        ForgetLoading();
    }

    /// <summary>
    /// Opens <paramref name="domain"/> as <paramref name="mode"/> says, telling <paramref name="progress"/> how far it
    /// is, 0 to 1. The task ends once the domain is there, and shown when it replaces the world, with it; with why not
    /// when its file does not load, before anything else is closed, or when it is closed before then. A prefab of it
    /// that does not load is an error, and it opens without it. Opening one already open keeps it, and closes the
    /// others when it replaces them. Any thread.
    /// </summary>
    internal async Task<Result<Domain>> OpenAsync(
        Handle<Domain> domain,
        DomainOpenMode mode,
        IProgress<float>? progress = null,
        CancellationToken cancel = default)
    {
        Result<Domain> file = await _assets.LoadAsync(domain, cancel).ConfigureAwait(false);

        if (!file.Ok) return Result.Failure<Domain>($"The domain does not open: {file.Error}");

        Open open = new(
            domain,
            _assets.GetPath(domain) ?? "A domain",
            file.Value,
            mode,
            isLoadingDomain: false,
            isReplacing: mode == DomainOpenMode.Replace);
        Open? already = await _threads.InvokeAsync(EngineThread.Main, () => Begin(open), cancel).ConfigureAwait(false);

        return already is not null
            ? Result.Success(already.Domain)
            : await ArriveAsync(open, progress, cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Tells that the world is shown, or that the domain replacing it is not coming, when that was so since the last
    /// frame began (<see cref="WorldLoading"/>), then takes in what workers finished since: a prefab read for a
    /// placement is spawned, a domain whose reading failed is closed, and one every gem readied is shown, as is a world
    /// put back every gem readied, which the next frame tells. On the main thread, at the start of each frame.
    /// </summary>
    internal void TakeArrived()
    {
        if (_isTelling)
        {
            _isTelling = false;
            _events.Publish(new WorldLoading(false));
        }

        // An arrival with no domain is a world put back, readied; one with a placement, a prefab read; one ready, a
        // domain readied; any other, a domain that is not coming, its reading failed or its open cancelled.
        while (_arrived.Reader.TryRead(out Arrived arrived))
        {
            if (arrived.Open is not { } open)
            {
                Replaced();
                Debugging.Info("The world put back is shown, every gem having readied it.");
            }
            else if (arrived.Placed is { } placed)
            {
                Add(open, placed, arrived.Prefab);
            }
            else if (arrived.IsReady)
            {
                Reveal(open);
            }
            else
            {
                Close(open);
            }
        }
    }

    /// <summary>
    /// Closes <paramref name="domain"/>, destroying what it spawned; nothing happens when it is not open.
    /// </summary>
    internal void Close(Handle<Domain> domain)
    {
        if (_domains.Find(open => open.Handle == domain) is { } found) Close(found);
    }

    /// <summary>
    /// Closes every open domain, destroying what each spawned, as a world put back closes what is open first.
    /// </summary>
    internal void CloseAll()
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
    internal List<DomainState> Save()
    {
        List<DomainState> domains = [];
        foreach (Open open in _domains)
        {
            Dictionary<Handle<Prefab>, IReadOnlyList<int>> prefabs = [];
            foreach ((Handle<Prefab> file, Placed placed) in open.Prefabs)
            {
                if (placed.Entities is { } spawned) prefabs[file] = GetIds(spawned);
            }

            domains.Add(new DomainState(open.Handle, open.Mode, prefabs, GetIds(open.Player ?? [])));
        }

        return domains;
    }

    /// <summary>
    /// The loading domain's entities, which a saved world leaves out: it is shown again as a domain opens anew.
    /// </summary>
    internal HashSet<Entity> GetLoadingEntities()
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
    /// Puts the saved domains <paramref name="saved"/> back in place of the world, once every entity of the saved world
    /// is made, <paramref name="made"/> by its saved id. The world loads, held out of sight from this call as one that
    /// replaced it is while it is readied: every gem readies it at once, so nothing that runs it runs, nor what is put
    /// back after this call, its scripts among it, until it is shown. Each domain opens again around the entities its
    /// prefabs had spawned and its player, its folder read on workers and taken in as when it opens, so a prefab added
    /// since is spawned, one gone has its entities destroyed, and the models its prefabs draw are held as they would be;
    /// once every one has, every gem readies the world again, and it is shown once they all have
    /// (<see cref="RevealRestoredAsync"/>). No loading domain is shown, as what was put back is there already, and no
    /// player is put. One that had spawned nothing, as one still opening, opens again from the start. A domain whose
    /// file does not load is an error, and is left out.
    /// </summary>
    internal void Restore(IReadOnlyList<DomainState> saved, Dictionary<int, Entity> made)
    {
        // Held out of sight from now: every gem readies the world at once, so nothing that runs it runs until it is
        // shown.
        BeginLoading();
        ReadyAllAsync("the world put back", _lifetime.Stopping).FireAndForget();

        // A domain that had spawned nothing opens again from the start; any other opens around the entities made for
        // what its prefabs and its player had spawned.
        List<Task> restoring = new(saved.Count);
        foreach (DomainState state in saved)
        {
            Task restore;
            if (state.Prefabs.Count == 0 && state.Player.Count == 0)
            {
                restore = RestoreAsync(state, null, null);
            }
            else
            {
                Dictionary<Handle<Prefab>, Entity[]> kept = new(state.Prefabs.Count);
                foreach ((Handle<Prefab> file, IReadOnlyList<int> ids) in state.Prefabs)
                {
                    kept[file] = GetEntities(ids, made);
                }

                restore = RestoreAsync(state, kept, state.Player.Count > 0 ? GetEntities(state.Player, made) : null);
            }

            restore.FireAndForget();
            restoring.Add(restore);
        }

        RevealRestoredAsync(restoring).FireAndForget();
    }

    /// <summary>
    /// The cell a prefab file named <c>x_y_z</c> is, as (x, y, z); null for any other name.
    /// </summary>
    private static Vector3? ParseCell(string file)
    {
        string[] parts = Path.GetFileNameWithoutExtension(file).Split('_');

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
                if (component is Renderer { Model: var model } && model != Handle<Model>.None) models.Add(model);
            }

            AddDrawn(part.Children, models);
        }
    }

    /// <summary>
    /// Where <paramref name="open"/>'s player is put: at its first entity tagged <see cref="Tag.Spawn"/>, in its
    /// prefabs that are not cells, then in its cells, turned as that entity is; else its origin, which is warned about.
    /// </summary>
    private static Transform SpawnOf(Open open)
    {
        // The prefabs that are not cells first, then the cells.
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
    /// The entities made for the saved <paramref name="ids"/>, those the save held.
    /// </summary>
    private static Entity[] GetEntities(IReadOnlyList<int> ids, Dictionary<int, Entity> made)
    {
        List<Entity> entities = new(ids.Count);
        foreach (int id in ids)
        {
            if (made.TryGetValue(id, out Entity entity)) entities.Add(entity);
        }

        return [.. entities];
    }

    /// <summary>
    /// <paramref name="preloader"/> readies <paramref name="what"/> (<see cref="IPreloader.ReadyAsync"/>): what it
    /// throws, but its being cancelled, is an error, and the world is shown without waiting for it. Awaited here, so
    /// one that throws as it is called fails this task rather than the call that readies the world.
    /// </summary>
    private static async Task ReadyOneAsync(IPreloader preloader, string what, CancellationToken cancel)
    {
        try
        {
            await preloader.ReadyAsync(cancel).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Debugging.Error($"Gem {preloader.GetType().Name} threw as it readied {what}, which is shown without it: "
                + $"{ex}");
        }
    }

    /// <summary>
    /// Starts opening <paramref name="open"/>, its file loaded: when it replaces the world, the open domains close and
    /// the loading domain is shown. The domain already open with its handle when there is one, which is kept.
    /// </summary>
    private Open? Begin(Open open)
    {
        Open? already = _domains.Find(other => other.Handle == open.Handle);
        if (open.IsReplacing)
        {
            for (int i = _domains.Count - 1; i >= 0; i--)
            {
                if (_domains[i] != already) Close(_domains[i]);
            }
        }

        if (already is not null) return already;

        if (open.IsReplacing && BeginLoading()) ShowLoading();

        _domains.Add(open);

        return null;
    }

    /// <summary>
    /// Something begins to replace the world, a domain opening in its place or a saved world put back: the world is
    /// loading until it is shown, told now (<see cref="WorldLoading"/>) unless it was loading already. Whether it was
    /// not.
    /// </summary>
    private bool BeginLoading()
    {
        if (_replacing++ > 0) return false;

        // The world loads again before the end of the last loading was told: to whoever hears it, it goes on.
        _isTelling = false;
        _events.Publish(new WorldLoading(true));

        return true;
    }

    /// <summary>
    /// Reads what <paramref name="open"/> spawns, then spawns it on the main thread, and when it replaces the world
    /// waits until it is shown (<see cref="RevealAsync"/>); closes it when reading is cancelled or throws, so a domain
    /// that is not coming never keeps the world loading.
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
            _arrived.Writer.TryWrite(new Arrived(open, null, default));

            throw;
        }

        // Whatever the token: what was read holds what the gems took in, which arriving keeps, or lets go when the
        // domain was closed meanwhile.
        Result<Domain> arrived = await _threads.InvokeAsync(
            EngineThread.Main,
            () => Arrive(open, read, cancel),
            CancellationToken.None)
            .ConfigureAwait(false);

        return arrived.Ok && open.IsReplacing ? await RevealAsync(open, cancel).ConfigureAwait(false) : arrived;
    }

    /// <summary>
    /// Waits until every gem has readied <paramref name="open"/>, spawned in place of the world (<see cref="Ready"/>),
    /// then has it shown as the next frame starts (<see cref="Reveal"/>), and ends once it is; closes it when the open
    /// is cancelled meanwhile, as one whose reading is.
    /// </summary>
    private async Task<Result<Domain>> RevealAsync(Open open, CancellationToken cancel)
    {
        await open.Readied.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (cancel.IsCancellationRequested)
        {
            _arrived.Writer.TryWrite(new Arrived(open, null, default));
            cancel.ThrowIfCancellationRequested();
        }

        _arrived.Writer.TryWrite(new Arrived(open, null, default, IsReady: true));

        return await open.Shown.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Every prefab in <paramref name="open"/>'s folder, each read on a worker, with the player when it replaces the
    /// world, and the models they draw loaded, then all of it taken in by the gems that need time to
    /// (<see cref="IPreloader"/>), so whatever draws or simulates them has them the frame they spawn, the loading
    /// domain's too, which is kept so (<see cref="ShowLoading"/>). The reads are the first half of the progress, the
    /// gems' taking in the second. On a worker.
    /// </summary>
    private async Task<Read> ReadAsync(Open open, IProgress<float>? progress, CancellationToken cancel)
    {
        List<Placement> placements = GetPlacements(open.Handle, open.Domain);
        int read = 0;
        Task<Result<Prefab>>[] reading = new Task<Result<Prefab>>[placements.Count];
        for (int i = 0; i < reading.Length; i++)
        {
            reading[i] = ReadOneAsync(placements[i].File);
        }

        Result<Prefab>[] prefabs = await Task.WhenAll(reading).ConfigureAwait(false);
        Prefab? player = null;
        if (open.IsReplacing && _project.Player != Handle<Prefab>.None)
        {
            Result<Prefab> loaded = await _assets.LoadAsync(_project.Player, cancel).ConfigureAwait(false);
            if (loaded.Ok) player = loaded.Value;
            else Debugging.Error($"The project's player is not spawned: {loaded.Error}");
        }

        List<Prefab> spawning = [];
        foreach (Result<Prefab> prefab in prefabs)
        {
            if (prefab.Ok) spawning.Add(prefab.Value);
        }

        if (player is not null) spawning.Add(player);

        // The models they draw are loaded too, and held by the domain while it is open, so whatever draws them has them
        // the frame they spawn.
        HashSet<Handle<Model>> drawn = [];
        foreach (Prefab prefab in spawning)
        {
            AddDrawn(prefab.Parts, drawn);
        }

        List<Task<Result<Model>>> loading = [];
        foreach (Handle<Model> model in drawn)
        {
            loading.Add(_assets.LoadAsync(model, cancel).AsTask());
        }

        List<Model> models = [];
        foreach (Result<Model> model in await Task.WhenAll(loading).ConfigureAwait(false))
        {
            if (model.Ok) models.Add(model.Value);
        }

        SubscriptionList holds = await PreloadAsync(spawning, progress, cancel).ConfigureAwait(false);
        progress?.Report(1);

        return new Read(placements, prefabs, player, models) { Holds = holds };

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
    private async Task<SubscriptionList> PreloadAsync(
        List<Prefab> spawning,
        IProgress<float>? progress,
        CancellationToken cancel)
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
                if (preload.IsCompletedSuccessfully) (await preload.ConfigureAwait(false)).Dispose();
            }

            throw;
        }

        return holds;
    }

    /// <summary>
    /// Every prefab in the folder of <paramref name="handle"/>, read as <paramref name="domain"/>, where it is spawned:
    /// a cell at its corner, any other at the origin. Any thread.
    /// </summary>
    private List<Placement> GetPlacements(Handle<Domain> handle, Domain domain)
    {
        string path = _assets.GetPath(handle) ?? string.Empty;
        int slash = path.LastIndexOf('/');
        string folder = slash < 0 ? string.Empty : path[..slash];
        List<Placement> placements = [];
        foreach (string file in _assets.GetPaths(folder))
        {
            Handle<Prefab> prefab = _assets.Find<Prefab>(file);

            if (prefab == Handle<Prefab>.None) continue;

            Vector3? cell = ParseCell(file);
            placements.Add(
                new Placement(prefab, cell is { } at ? at * domain.CellSize : Vector3.Zero, cell.HasValue));
        }

        return placements;
    }

    /// <summary>
    /// What <paramref name="open"/> spawns is read: spawned now, all of it as one spawn, but what a domain put back
    /// kept (<see cref="Reopen"/>), which only takes its prefab as it is now; the player at its spawn when it replaces
    /// the world; unless it was closed meanwhile. A domain that replaces the world takes the loading domain's place in
    /// this frame, out of sight, and is readied (<see cref="Ready"/>) until <paramref name="cancel"/>.
    /// </summary>
    private Result<Domain> Arrive(Open open, Read read, CancellationToken cancel)
    {
        if (open.IsClosed)
        {
            read.Holds.Dispose();

            return Result.Failure<Domain>("It was closed before it was there.");
        }

        // What the gems took in is kept while the domain is open. Each prefab spawns, but one a domain put back already
        // has entities for, which only takes its prefab as it is now.
        open.Holds = read.Holds;
        int entities = 0;
        for (int i = 0; i < read.Placements.Count; i++)
        {
            Placement placement = read.Placements[i];
            if (!open.Prefabs.TryGetValue(placement.File, out Placed? placed))
            {
                placed = new Placed(placement);
                open.Prefabs[placement.File] = placed;
            }

            if (read.Prefabs[i] is { Ok: true, Value: { } prefab })
            {
                if (placed.Entities is null) Show(placed, prefab);
                else placed.Prefab = prefab;

                entities += placed.Entities?.Length ?? 0;
            }
            else
            {
                Debugging.Error($"{_assets.GetPath(placement.File) ?? "A prefab"} of {open.Name} is left out: "
                    + $"{read.Prefabs[i].Error}");
            }
        }

        open.Models = read.Models;
        if (read.Player is { } player) open.Player = _world.Spawn(player, SpawnOf(open));

        open.IsThere = true;
        if (!open.IsLoadingDomain)
            Debugging.Info($"Opened {open.Name}: {entities} entities in {open.Prefabs.Count} prefabs.");

        if (open.IsReplacing) Ready(open, cancel);

        return Result.Success(open.Domain);
    }

    /// <summary>
    /// Readies <paramref name="open"/>, spawned in place of the world: the loading domain closes, its last image left on
    /// the window, and every gem that took the domain in readies it, all at once (<see cref="IPreloader.ReadyAsync"/>),
    /// until <paramref name="cancel"/> or the domain closing stops them.
    /// </summary>
    private void Ready(Open open, CancellationToken cancel)
    {
        if (_loading is { } loading) Close(loading);

        open.Readying = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        open.Readied = ReadyAllAsync(open.Name, open.Readying.Token);
    }

    /// <summary>
    /// Every gem that took in what spawns readies the world, <paramref name="what"/> having been put in its place, all
    /// at once (<see cref="IPreloader.ReadyAsync"/>), until <paramref name="cancel"/>: the task ends once every one has,
    /// or stopped.
    /// </summary>
    private Task ReadyAllAsync(string what, CancellationToken cancel)
    {
        IReadOnlyList<IPreloader> preloaders = _world.Preloaders;
        Task[] readying = new Task[preloaders.Count];
        for (int i = 0; i < readying.Length; i++)
        {
            readying[i] = ReadyOneAsync(preloaders[i], what, cancel);
        }

        return Task.WhenAll(readying);
    }

    /// <summary>
    /// Shows <paramref name="open"/> from this frame, every gem having readied it: the loading ends, told as the next
    /// frame starts; unless it was closed meanwhile.
    /// </summary>
    private void Reveal(Open open)
    {
        open.Readying?.Dispose();
        open.Readying = null;
        if (open.IsClosed)
        {
            open.Shown.TrySetResult(Result.Failure<Domain>("It was closed before it was shown."));

            return;
        }

        open.IsShown = true;
        Replaced();
        Debugging.Info($"{open.Name} is shown, every gem having readied it.");
        open.Shown.TrySetResult(Result.Success(open.Domain));
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
    private int[] GetIds(Entity[] entities)
    {
        List<int> ids = new(entities.Length);
        foreach (Entity entity in entities)
        {
            if (_ecs.IsAlive(entity)) ids.Add(entity.Id);
        }

        return [.. ids];
    }

    /// <summary>
    /// Puts the saved domain <paramref name="saved"/> back (<see cref="Restore"/>): its file loaded, it opens around
    /// <paramref name="kept"/>, the entities made for the ones each of its prefabs had spawned, by file, and
    /// <paramref name="player"/>, the player's, then is read and arrives as a domain opening does. One that had
    /// spawned nothing, <paramref name="kept"/> null, opens again from the start.
    /// </summary>
    private async Task RestoreAsync(DomainState saved, Dictionary<Handle<Prefab>, Entity[]>? kept, Entity[]? player)
    {
        Result<Domain> file = await _assets.LoadAsync(saved.Domain, _lifetime.Stopping).ConfigureAwait(false);
        if (!file.Ok)
        {
            Debugging.Error($"{_assets.GetPath(saved.Domain) ?? "A domain"} is not restored: {file.Error}");

            return;
        }

        Open open = new(
            saved.Domain,
            _assets.GetPath(saved.Domain) ?? "A domain",
            file.Value,
            saved.Mode,
            isLoadingDomain: false,
            isReplacing: kept is null && saved.Mode == DomainOpenMode.Replace);
        bool isOpening = await _threads.InvokeAsync(
            EngineThread.Main,
            () =>
            {
                if (kept is null) return Begin(open) is null;

                Reopen(open, kept, player);

                return true;
            },
            _lifetime.Stopping).ConfigureAwait(false);

        if (isOpening) await ArriveAsync(open, null, _lifetime.Stopping).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits until every domain of a world put back has arrived, <paramref name="restoring"/>, or is not coming, then
    /// has every gem ready the world again, as what they took in and spawned is there now, which takes the place of the
    /// readying it was held by as it was put back (<see cref="Restore"/>); once they all have, it is shown as the next
    /// frame starts (<see cref="TakeArrived"/>).
    /// </summary>
    private async Task RevealRestoredAsync(List<Task> restoring)
    {
        await Task.WhenAll(restoring).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        Task readied = await _threads.InvokeAsync(
            EngineThread.Main,
            () => ReadyAllAsync("the world put back", _lifetime.Stopping),
            _lifetime.Stopping)
            .ConfigureAwait(false);
        await readied.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _arrived.Writer.TryWrite(new Arrived(null, null, default));
    }

    /// <summary>
    /// Opens <paramref name="open"/> around what it had spawned when the world was saved: <paramref name="kept"/>, the
    /// entities made for each of its prefabs', by file, and <paramref name="player"/>, the player's. Each prefab now in
    /// its folder takes its entities; one with none, added since, is read and spawned as the domain arrives; the
    /// entities of a prefab gone since are destroyed. Nothing closes and no loading domain is shown: the world is not
    /// replaced.
    /// </summary>
    private void Reopen(Open open, Dictionary<Handle<Prefab>, Entity[]> kept, Entity[]? player)
    {
        _domains.Add(open);
        open.Player = player;
        foreach (Placement placement in GetPlacements(open.Handle, open.Domain))
        {
            Placed placed = new(placement);
            if (kept.Remove(placement.File, out Entity[]? entities)) placed.Entities = entities;

            open.Prefabs[placement.File] = placed;
        }

        foreach (Entity[] gone in kept.Values)
        {
            Destroy(gone);
        }
    }

    /// <summary>
    /// Takes <paramref name="open"/> out of the world, destroying what it spawned and its player; one being readied
    /// stops being.
    /// </summary>
    private void Close(Open open)
    {
        if (open.IsClosed) return;

        open.IsClosed = true;
        if (open.IsLoadingDomain) _loading = null;
        else _domains.Remove(open);

        foreach (Placed placed in open.Prefabs.Values)
        {
            Destroy(placed.Entities);
        }

        open.Prefabs.Clear();
        open.Models = [];
        open.Holds.Dispose();
        Destroy(open.Player);
        open.Readying?.Cancel();
        open.Readying?.Dispose();
        open.Readying = null;

        // One closed before it replaced the world is not coming: the world waits for it no more.
        if (!open.IsShown && open.IsReplacing) Replaced();
    }

    /// <summary>
    /// Shows the loading domain as a domain begins to replace the world: the project's, or the engine's. Kept, it
    /// spawns now, whole, so it is drawn with all it draws from the frame the open domains close. The first time it is
    /// read and taken in by every gem that needs time to, then kept for the run, and shown once it is unless the domain
    /// replacing the world is there by then (<see cref="KeepLoadingAsync"/>).
    /// </summary>
    private void ShowLoading()
    {
        if (_keeping is { Kept: { } kept })
        {
            ShowKept(kept);

            return;
        }

        if (_keeping is not null) return;

        string path = LoadingDomain + Domain.Extensions[0];
        Handle<Domain> handle = _project.Loading == Handle<Domain>.None ? _assets.Find<Domain>(path) : _project.Loading;
        if (handle == Handle<Domain>.None)
        {
            Debugging.Warn($"There is no loading domain: the project names none, and there is no {path}.");

            return;
        }

        _keeping = new Keeping(handle);
        KeepLoadingAsync(_keeping).FireAndForget();
    }

    /// <summary>
    /// Reads the loading domain <paramref name="keeping"/> names and has every gem that needs time to take it in, then
    /// keeps it, on the main thread, and shows it if the world is still loading and the domain replacing it is not there
    /// yet; lets it go when it was forgotten meanwhile (<see cref="ForgetLoading"/>). One that does not open is an error,
    /// and none is shown until its files change.
    /// </summary>
    private async Task KeepLoadingAsync(Keeping keeping)
    {
        Result<Domain> file = await _assets.LoadAsync(keeping.Handle, _lifetime.Stopping).ConfigureAwait(false);
        if (!file.Ok)
        {
            Debugging.Error($"The loading domain does not open: {file.Error}");

            return;
        }

        string name = _assets.GetPath(keeping.Handle) ?? "The loading domain";
        Open reading = new(
            keeping.Handle,
            name,
            file.Value,
            DomainOpenMode.Additive,
            isLoadingDomain: true,
            isReplacing: false);
        Read read = await ReadAsync(reading, null, _lifetime.Stopping).ConfigureAwait(false);
        await _threads.InvokeAsync(
            EngineThread.Main,
            () =>
            {
                if (keeping.IsForgotten)
                {
                    read.Holds.Dispose();

                    return;
                }

                keeping.Kept = new Resident(keeping.Handle, name, file.Value, read);

                // Shown only while the world loads with no loading domain shown, and not once the domain replacing the
                // world is there, being readied behind the loading domain's last image.
                if (!IsLoading || _loading is not null || _domains.Exists(static open => open.Readying is not null))
                    return;

                ShowKept(keeping.Kept);
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// Spawns the kept loading domain <paramref name="kept"/> now, as it was read; what keeps it taken in stays when it
    /// closes.
    /// </summary>
    private void ShowKept(Resident kept)
    {
        _loading = new Open(
            kept.Handle,
            kept.Name,
            kept.Domain,
            DomainOpenMode.Additive,
            isLoadingDomain: true,
            isReplacing: false);
        Arrive(_loading, kept.Read with { Holds = new SubscriptionList() }, CancellationToken.None);
    }

    /// <summary>
    /// Lets the kept loading domain go, as its files changed, so the next domain to replace the world reads and takes
    /// it in again; one being read is let go once it is. One shown stays as it is until it closes.
    /// </summary>
    private void ForgetLoading()
    {
        if (_keeping is not { } keeping) return;

        _keeping = null;
        keeping.IsForgotten = true;
        keeping.Kept?.Read.Holds.Dispose();
    }

    /// <summary>
    /// A domain that was replacing the world is shown, or is not coming: once none is left, the loading ends, the
    /// loading domain closing if it is still shown, and what was held while it loaded starts as the next frame does,
    /// told by <see cref="WorldLoading"/>.
    /// </summary>
    private void Replaced()
    {
        if (--_replacing > 0) return;

        _isTelling = true;
        if (_loading is { } loading) Close(loading);
    }

    private void Destroy(Entity[]? entities)
    {
        foreach (Entity entity in entities ?? [])
        {
            if (_ecs.IsAlive(entity)) _ecs.Destroy(entity);
        }
    }

    /// <summary>
    /// The domains open, and the loading domain while it is shown.
    /// </summary>
    private List<Open> GetShown()
    {
        List<Open> shown = [.. _domains];
        if (_loading is { } loading) shown.Add(loading);

        return shown;
    }

    /// <summary>
    /// A domain's file was read again: what it is now, which <see cref="FilesChanged"/>, told next, places its cells
    /// by. The kept loading domain's is read again with it the next time it shows.
    /// </summary>
    private void DomainReloaded(AssetReloaded<Domain> reloaded)
    {
        if (_keeping?.Handle == reloaded.Asset.Handle) ForgetLoading();

        foreach (Open open in GetShown())
        {
            if (open.Handle == reloaded.Asset.Handle) open.Domain = reloaded.Asset;
        }
    }

    /// <summary>
    /// A prefab was read again, as its file or a prefab it places changed: wherever a domain spawned it, it is spawned
    /// again as it is now, and the kept loading domain is read again the next time it shows when it is one of its. The
    /// player is not: what the player has done is the game's.
    /// </summary>
    private void PrefabReloaded(AssetReloaded<Prefab> reloaded)
    {
        if (_keeping?.Kept?.Read.Placements.Exists(placement => placement.File == reloaded.Asset.Handle) == true)
            ForgetLoading();

        foreach (Open open in GetShown())
        {
            if (open.IsThere && open.Prefabs.TryGetValue(reloaded.Asset.Handle, out Placed? placed))
                Show(placed, reloaded.Asset);
        }
    }

    /// <summary>
    /// Files came, went, moved or changed: in each domain that is there, a prefab that came is read and spawned, one
    /// that went is destroyed, one that moved, or whose cell moved with the cell size, is spawned where it is now, and
    /// one that did not load is tried again. The kept loading domain is read again the next time it shows when its
    /// folder no longer holds the prefabs it was read with.
    /// </summary>
    private void FilesChanged(AssetsChanged changed)
    {
        if (_keeping?.Kept is { } kept && !GetPlacements(kept.Handle, kept.Domain).SequenceEqual(kept.Read.Placements))
            ForgetLoading();

        foreach (Open open in GetShown())
        {
            if (!open.IsThere) continue;

            // Each prefab in its folder now: one spawned where it still is stays, and any other, come, moved or not
            // loaded, is read again and spawned in its place.
            HashSet<Handle<Prefab>> present = [];
            foreach (Placement placement in GetPlacements(open.Handle, open.Domain))
            {
                present.Add(placement.File);

                if (open.Prefabs.TryGetValue(placement.File, out Placed? placed) && placed.Placement == placement
                    && placed.Entities is not null)
                {
                    continue;
                }

                if (placed is not null) Destroy(placed.Entities);

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
        Result<Prefab> prefab = await _assets.LoadAsync(placed.Placement.File, _lifetime.Stopping)
            .ConfigureAwait(false);
        _arrived.Writer.TryWrite(new Arrived(open, placed, prefab));
    }

    /// <summary>
    /// Spawns <paramref name="placed"/>'s prefab, read as <paramref name="prefab"/>, unless its domain closed, or
    /// another took its place, by then.
    /// </summary>
    private void Add(Open open, Placed placed, Result<Prefab> prefab)
    {
        if (open.IsClosed || !open.Prefabs.TryGetValue(placed.Placement.File, out Placed? now) || now != placed) return;

        if (prefab.Ok)
        {
            Show(placed, prefab.Value);
        }
        else
        {
            Debugging.Error(
                $"{_assets.GetPath(placed.Placement.File) ?? "A prefab"} of {open.Name} is left out: {prefab.Error}");
        }
    }

    /// <summary>
    /// A prefab of a domain, and where it is spawned: <see cref="Corner"/>, a cell's, or the domain's origin.
    /// </summary>
    private readonly record struct Placement(Handle<Prefab> File, Vector3 Corner, bool IsCell);

    /// <summary>
    /// What a worker finished for a domain: <paramref name="Placed"/>'s prefab read as <paramref name="Prefab"/>; or,
    /// with no placement, that every gem readied it, when <paramref name="IsReady"/>, and it is shown, else that reading
    /// it failed and it closes. With no domain, that every gem readied a world put back, which is shown.
    /// </summary>
    private readonly record struct Arrived(Open? Open, Placed? Placed, Result<Prefab> Prefab, bool IsReady = false);

    /// <summary>
    /// What a domain's opening read: each of its prefabs, by placement, its player, the models they draw, and what
    /// the gems that took them in handed back, which keeps that taken in (<see cref="IPreloader"/>).
    /// </summary>
    private sealed record Read(List<Placement> Placements, Result<Prefab>[] Prefabs, Prefab? Player, List<Model> Models)
    {
        /// <summary>
        /// Set by an initializer rather than the constructor, so the analyzer sees the record take ownership of it.
        /// </summary>
        internal required SubscriptionList Holds { get; init; }
    }

    /// <summary>
    /// The loading domain as it was read to be kept: its handle, its path for messages, its file, and what was read for
    /// it, whose holds keep it taken in while it is kept (<see cref="Keeping"/>).
    /// </summary>
    private sealed record Resident(Handle<Domain> Handle, string Name, Domain Domain, Read Read);

    /// <summary>
    /// The loading domain being read to be kept, or kept (<see cref="Kept"/>), and whether it was let go meanwhile, as
    /// its files changed, so what is read for it then is let go too. Main thread.
    /// </summary>
    private sealed class Keeping(Handle<Domain> handle)
    {
        internal Handle<Domain> Handle { get; } = handle;

        /// <summary>
        /// What was read for it; none while it is being read, or when it does not open.
        /// </summary>
        internal Resident? Kept { get; set; }

        internal bool IsForgotten { get; set; }
    }

    /// <summary>
    /// A domain open in the world, or opening.
    /// </summary>
    private sealed class Open(
        Handle<Domain> handle,
        string name,
        Domain domain,
        DomainOpenMode mode,
        bool isLoadingDomain,
        bool isReplacing)
    {
        internal Handle<Domain> Handle { get; } = handle;

        /// <summary>
        /// Its path, for messages.
        /// </summary>
        internal string Name { get; } = name;

        /// <summary>
        /// Its file, as it is now.
        /// </summary>
        internal Domain Domain { get; set; } = domain;

        internal DomainOpenMode Mode { get; } = mode;

        /// <summary>
        /// It is the loading domain, which is never among the domains open.
        /// </summary>
        internal bool IsLoadingDomain { get; } = isLoadingDomain;

        /// <summary>
        /// It replaces the world: the open domains close as it begins, the loading domain shows until it is there, then
        /// its last image until every gem has readied it and it is shown, and the player is put at its spawn. Never the
        /// loading domain, nor a domain put back from a saved world, whose player is among the entities put back.
        /// </summary>
        internal bool IsReplacing { get; } = isReplacing;

        /// <summary>
        /// Everything it spawns is spawned.
        /// </summary>
        internal bool IsThere { get; set; }

        /// <summary>
        /// It replaced the world and is shown, every gem having readied it.
        /// </summary>
        internal bool IsShown { get; set; }

        /// <summary>
        /// What stops the gems readying it, closing it or the open being cancelled, while they are; none before and
        /// after.
        /// </summary>
        internal CancellationTokenSource? Readying { get; set; }

        /// <summary>
        /// Ends once every gem has readied it, or stopped (<see cref="Readying"/>).
        /// </summary>
        internal Task Readied { get; set; } = Task.CompletedTask;

        /// <summary>
        /// Ends once it is shown, with it, or with why not when it was closed first.
        /// </summary>
        internal TaskCompletionSource<Result<Domain>> Shown { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// It was closed: what it read since is dropped.
        /// </summary>
        internal bool IsClosed { get; set; }

        /// <summary>
        /// Its prefabs, by file.
        /// </summary>
        internal Dictionary<Handle<Prefab>, Placed> Prefabs { get; } = [];

        /// <summary>
        /// The models its prefabs drew as it opened, held while it is open so whatever draws them keeps them.
        /// </summary>
        internal List<Model> Models { get; set; } = [];

        /// <summary>
        /// What the gems that took in its prefabs before they spawned handed back, held while it is open so they keep
        /// that taken in (<see cref="IPreloader"/>).
        /// </summary>
        internal SubscriptionList Holds { get; set; } = new();

        /// <summary>
        /// The project's player, when it replaced the world, or the one kept as it was put back.
        /// </summary>
        internal Entity[]? Player { get; set; }
    }

    /// <summary>
    /// A prefab of a domain where it is spawned, and what it spawned: none yet, or when it did not load.
    /// </summary>
    private sealed class Placed(Placement placement)
    {
        internal Placement Placement { get; } = placement;

        internal Prefab? Prefab { get; set; }

        internal Entity[]? Entities { get; set; }
    }

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
