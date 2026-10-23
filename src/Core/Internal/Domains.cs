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
/// it readied by those gems out of sight before it is shown, the world emptied each time something takes its place
/// (<see cref="EmptyWorld"/>), closing one, each kept as its files change, and the domains' part of a saved world
/// (<see cref="Save"/>, <see cref="Restore"/>), readied out of sight too as it is put back. The one place where a load
/// stands: as each frame begins it asks the gems how far they are, moves every domain on that it can, and writes what
/// streams into the world down as one value (<see cref="Stream"/>), which the gems read and a gem below the world hears
/// on <see cref="Events"/>. The world makes one when a gem provides the ECS, and is the one place to act on it: nothing
/// else holds this. Main thread, but where a member says otherwise.
/// </summary>
internal sealed class Domains : IDisposable
{
    /// <summary>
    /// The engine's loading domain, but its extension.
    /// </summary>
    private const string DefaultLoadingDomainPath = "Domains/Loading/Loading";

    private readonly World _world;
    private readonly IEcs _ecs;
    private readonly Assets _assets;
    private readonly Threads _threads;
    private readonly App _app;
    private readonly Events _events;
    private readonly SubscriptionList _watching = new();

    /// <summary>
    /// The open domains, in the order they were opened, those still loading among them; never the loading domain.
    /// </summary>
    private readonly List<OpenDomain> _domains = [];

    /// <summary>
    /// What workers finished for the main thread to take in at the start of a frame (<see cref="Pump"/>): a domain
    /// read, a prefab read for a placement, or a domain whose reading failed or whose open was cancelled, which closes.
    /// </summary>
    private readonly Channel<HandOffToMainThread> _handOffsToMainThread = Channel.CreateUnbounded<HandOffToMainThread>(
        new UnboundedChannelOptions { SingleReader = true });

    /// <summary>
    /// What the domains spawn and are about to, and of that what is still taken in, gathered as each frame's stream is
    /// written down (<see cref="WriteDownStream"/>) and kept in it only when it changed.
    /// </summary>
    private readonly List<Prefab> _gatheredPrefabs = [];
    private readonly List<Prefab> _gatheredTakingIn = [];

    /// <summary>
    /// The loading domain while it is shown.
    /// </summary>
    private OpenDomain? _shownLoadingDomain;

    /// <summary>
    /// The loading domain, read and taken in by every gem that needs time to the first time a domain replaced the
    /// world, then kept so, so it is shown whole from the frame the open domains close
    /// (<see cref="ShowLoadingDomain"/>); none before, or once its files changed (<see cref="DropKeptLoadingDomain"/>).
    /// </summary>
    private KeptLoadingDomain? _keptLoadingDomain;

    /// <summary>
    /// Domains opening in place of the world and saved worlds put back in its place (<see cref="Restore"/>), not shown
    /// yet: the world is loading while there is one.
    /// </summary>
    private int _replacementsUnderWay;

    /// <summary>
    /// The saved domains of a world being put back that have yet to join it, their files still loading; none while no
    /// world is being put back. It is readied once none is left and every one has arrived.
    /// </summary>
    private int? _savedDomainsToJoin;

    /// <summary>
    /// Where the world is in having something take its place, and the frame it got there, -1 until the stream is next
    /// written down (<see cref="EnterStreamState"/>).
    /// </summary>
    private StreamState _streamState;
    private int _framesInStreamState;

    internal Domains(
        World world,
        IEcs ecs,
        Assets assets,
        Threads threads,
        Events events,
        App app)
    {
        _world = world;
        _ecs = ecs;
        _assets = assets;
        _threads = threads;
        _app = app;
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
            foreach (OpenDomain open in _domains)
            {
                opened.Add(open.Handle);
            }

            return opened;
        }
    }

    /// <summary>
    /// What streams into the world as it stands this frame (<see cref="World.Stream"/>), written down as it began
    /// (<see cref="WriteDownStream"/>).
    /// </summary>
    internal WorldStream Stream { get; private set; } = WorldStream.None;

    /// <summary>
    /// Whether the world has loaded: a domain other than the loading domain is open, every open one is there, spawned,
    /// and none is opening in place of the world, nor a saved world being put back, so what replaced it is shown.
    /// </summary>
    internal bool IsLoaded => _replacementsUnderWay == 0
        && _domains.Exists(static open => !open.IsLoadingDomain)
        && _domains.TrueForAll(static open => open.Stage >= Stage.Spawned);

    public void Dispose()
    {
        _watching.Dispose();
        DropKeptLoadingDomain();
    }

    /// <summary>
    /// Opens <paramref name="domain"/> as <paramref name="mode"/> says, telling <paramref name="progress"/> how far it
    /// is, 0 to 1. The task ends once the domain is there, and shown when it replaces the world, with it; with why not
    /// when its file does not load, before anything else is closed, or when it is closed before then; cancelled when
    /// <paramref name="cancel"/> closes it first. A prefab of it that does not load is an error, and it opens without
    /// it. Opening one already open keeps it, and closes the others when it replaces them. Any thread.
    /// </summary>
    internal async Task<Result<Domain>> OpenAsync(
        Handle<Domain> domain,
        DomainOpenMode mode,
        IProgress<float>? progress = null,
        CancellationToken cancel = default)
    {
        Result<Domain> file = await _assets.LoadAsync(domain, cancel).ConfigureAwait(false);

        if (!file.Ok) return Result.Failure<Domain>($"The domain does not open: {file.Error}");

        OpenDomain open = new(
            domain,
            _assets.GetPath(domain) ?? "A domain",
            file.Value,
            mode,
            isLoadingDomain: false,
            isReplacing: mode == DomainOpenMode.Replace)
        {
            Progress = progress
        };
        OpenDomain? already = await _threads.PostAsync(EngineThread.Main, () => StartOpeningDomain(open), cancel)
            .ConfigureAwait(false);

        if (already is not null) return Result.Success(already.Domain);

        // Cancelled before it is done, it closes as one whose reading failed does, and the world waits for it no more.
        Result<Domain> opened;
        await using (cancel.Register(() => _handOffsToMainThread.Writer.TryWrite(new HandOffToMainThread(open)))
            .ConfigureAwait(false))
        {
            ReadAndHandOffDomainAsync(open, cancel).FireAndForget();
            opened = await open.Opened.Task.ConfigureAwait(false);
        }

        if (!opened.Ok) cancel.ThrowIfCancellationRequested();

        return opened;
    }

    /// <summary>
    /// As each frame begins, on the main thread: the world shown last frame runs from this one, what workers finished
    /// since is taken in, the gems are asked how far they are with what the world streamed in as they last saw it, so
    /// every domain they have taken in spawns and a world they have readied is shown, then what streams in now is
    /// written down (<see cref="Stream"/>) and told when it changed.
    /// </summary>
    internal void Pump()
    {
        if (_streamState == StreamState.Shown) EnterStreamState(StreamState.Loaded);

        TakeInHandOffs();
        AskPreloaders();
        WriteDownStream();
    }

    /// <summary>
    /// Closes <paramref name="domain"/>, destroying what it spawned; nothing happens when it is not open.
    /// </summary>
    internal void Close(Handle<Domain> domain)
    {
        if (_domains.Find(open => open.Handle == domain) is { } found) Close(found);
    }

    /// <summary>
    /// Empties the world for a saved one put back in its place: every open domain closes, and every entity left is
    /// destroyed, its globals too, as the saved world holds its own (<see cref="EmptyWorld"/>).
    /// </summary>
    internal void EmptyWorldForRestore()
    {
        for (int i = _domains.Count - 1; i >= 0; i--)
        {
            Close(_domains[i]);
        }

        EmptyWorld("the world put back", keepsGlobals: false);
    }

    /// <summary>
    /// Each open domain as a saved world holds it (<see cref="World.SaveAsync"/>): which, how it was opened, and the
    /// entities each of its prefabs spawned and its player, those alive, by id. Never the loading domain.
    /// </summary>
    internal List<DomainState> Save()
    {
        List<DomainState> domains = [];
        foreach (OpenDomain open in _domains)
        {
            Dictionary<Handle<Prefab>, IReadOnlyList<int>> prefabs = [];
            foreach ((Handle<Prefab> file, PlacedPrefab placed) in open.Prefabs)
            {
                if (placed.Entities is { } spawned) prefabs[file] = GetAliveIds(spawned);
            }

            domains.Add(new DomainState(open.Handle, open.Mode, prefabs, GetAliveIds(open.Player ?? [])));
        }

        return domains;
    }

    /// <summary>
    /// The loading domain's entities, which a saved world leaves out: it is shown again as a domain opens anew.
    /// </summary>
    internal HashSet<Entity> GetLoadingEntities() => _shownLoadingDomain is { } shown ? GetSpawned([shown]) : [];

    /// <summary>
    /// Puts the saved domains <paramref name="saved"/> back in place of the world, once every entity of the saved world
    /// is made, <paramref name="made"/> by its saved id. The world is readied out of sight from this call
    /// (<see cref="StreamState.Readying"/>), so nothing that runs it runs, nor what is put back after this call, its
    /// scripts among it, until it is shown. Each domain opens again around the entities its prefabs had spawned and its
    /// player, its folder read on workers and taken in as when it opens, so a prefab added since is spawned, one gone
    /// has its entities destroyed, and the models its prefabs draw are held as they would be; once every one has, the
    /// gems are asked whether the world is ready, and it is shown once they all say so. No loading domain is shown, as
    /// what was put back is there already, and no player is put. One that had spawned nothing, as one still opening,
    /// opens again from the start. A domain whose file does not load is an error, and is left out.
    /// </summary>
    internal void Restore(IReadOnlyList<DomainState> saved, Dictionary<int, Entity> made)
    {
        _replacementsUnderWay++;
        _savedDomainsToJoin = saved.Count;
        EnterStreamState(StreamState.Readying);

        // A domain that had spawned nothing opens again from the start; any other opens around the entities made for
        // what its prefabs and its player had spawned.
        foreach (DomainState state in saved)
        {
            if (state.Prefabs.Count == 0 && state.Player.Count == 0)
            {
                RestoreAsync(state, null, null).FireAndForget();

                continue;
            }

            Dictionary<Handle<Prefab>, Entity[]> kept = new(state.Prefabs.Count);
            foreach ((Handle<Prefab> file, IReadOnlyList<int> ids) in state.Prefabs)
            {
                kept[file] = GetEntitiesMadeFor(ids, made);
            }

            RestoreAsync(state, kept, state.Player.Count > 0 ? GetEntitiesMadeFor(state.Player, made) : null)
                .FireAndForget();
        }
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
    private static void AddDrawnModels(IReadOnlyList<PrefabPart> parts, HashSet<Handle<Model>> models)
    {
        foreach (PrefabPart part in parts)
        {
            foreach (IComponent component in part.Components)
            {
                if (component is Renderer { Model: var model } && model != Handle<Model>.None) models.Add(model);
            }

            AddDrawnModels(part.Children, models);
        }
    }

    /// <summary>
    /// Where <paramref name="open"/>'s player is put: at its first entity tagged <see cref="Tag.Spawn"/>, in its
    /// prefabs that are not cells, then in its cells, turned as that entity is; else its origin, which is warned about.
    /// </summary>
    private static Transform GetPlayerSpawn(OpenDomain open)
    {
        // The prefabs that are not cells first, then the cells.
        foreach (bool cells in (ReadOnlySpan<bool>)[false, true])
        {
            foreach (PlacedPrefab placed in open.Prefabs.Values)
            {
                if (placed.Placement.IsCell == cells && placed.Prefab?.FindWith(Tag.Spawn) is { } spawn)
                {
                    Transform at = spawn.Under(new Transform { Position = placed.Placement.Corner });

                    return new Transform { Position = at.Position, Rotation = at.Rotation };
                }
            }
        }

        Debugging.Warn($"{open.Path} has no entity tagged spawn, so the player is put at its origin.");

        return new Transform();
    }

    /// <summary>
    /// The entities <paramref name="domains"/> spawned: their prefabs' and their players'.
    /// </summary>
    private static HashSet<Entity> GetSpawned(IReadOnlyList<OpenDomain> domains)
    {
        HashSet<Entity> spawned = [];
        foreach (OpenDomain open in domains)
        {
            foreach (PlacedPrefab placed in open.Prefabs.Values)
            {
                spawned.UnionWith(placed.Entities ?? []);
            }

            spawned.UnionWith(open.Player ?? []);
        }

        return spawned;
    }

    /// <summary>
    /// The entities made for the saved <paramref name="ids"/>, those the save held.
    /// </summary>
    private static Entity[] GetEntitiesMadeFor(IReadOnlyList<int> ids, Dictionary<int, Entity> made)
    {
        List<Entity> entities = new(ids.Count);
        foreach (int id in ids)
        {
            if (made.TryGetValue(id, out Entity entity)) entities.Add(entity);
        }

        return [.. entities];
    }

    /// <summary>
    /// Whether <paramref name="now"/> holds the same prefabs as <paramref name="was"/>, in the same order.
    /// </summary>
    private static bool HasSamePrefabs(List<Prefab> now, IReadOnlyList<Prefab> was)
    {
        if (now.Count != was.Count) return false;

        for (int i = 0; i < now.Count; i++)
        {
            if (now[i] != was[i]) return false;
        }

        return true;
    }

    /// <summary>
    /// What <paramref name="read"/> spawns that loaded, its prefabs and its player, into <paramref name="into"/>.
    /// </summary>
    private static void AddPrefabsToSpawn(ReadDomain read, List<Prefab> into)
    {
        foreach (Result<Prefab> prefab in read.Prefabs)
        {
            if (prefab.Ok) into.Add(prefab.Value);
        }

        if (read.Player is { } player) into.Add(player);
    }

    /// <summary>
    /// Starts opening <paramref name="open"/>, its file loaded: when it replaces the world, the open domains close, the
    /// world empties for it (<see cref="EmptyWorld"/>), and, unless a saved world is being put back, the world loads
    /// with the loading domain shown. The domain already open with its handle when there is one, which is kept.
    /// </summary>
    private OpenDomain? StartOpeningDomain(OpenDomain open)
    {
        OpenDomain? already = _domains.Find(other => other.Handle == open.Handle);
        if (open.IsReplacing)
        {
            for (int i = _domains.Count - 1; i >= 0; i--)
            {
                if (_domains[i] != already) Close(_domains[i]);
            }
        }

        if (already is not null) return already;

        if (open.IsReplacing)
        {
            EmptyWorld(open.Path, keepsGlobals: true);
            _replacementsUnderWay++;
            if (_savedDomainsToJoin is null)
            {
                EnterStreamState(StreamState.Loading);
                if (_shownLoadingDomain is null) ShowLoadingDomain();
            }
        }

        _domains.Add(open);

        return null;
    }

    /// <summary>
    /// The world is now at <paramref name="state"/>, from its first frame, which the stream says as it is next written
    /// down (<see cref="WriteDownStream"/>).
    /// </summary>
    private void EnterStreamState(StreamState state)
    {
        _streamState = state;
        _framesInStreamState = -1;
    }

    /// <summary>
    /// The world empties as <paramref name="what"/> takes its place, before anything of it spawns: every entity no
    /// domain shown spawned is destroyed, what scripts and gems made among them, but the world's globals when it
    /// <paramref name="keepsGlobals"/> (<see cref="World.CreateGlobal"/>). Whoever empties it enters the state it is
    /// emptied for, from whose first frame every gem forgets the old world (<see cref="WorldStream.IsEmptied"/>).
    /// </summary>
    private void EmptyWorld(string what, bool keepsGlobals)
    {
        HashSet<Entity> kept = GetSpawned(GetOpenAndLoadingDomains());
        if (keepsGlobals) kept.UnionWith(_world.GetGlobals());

        List<Entity> left = [];
        foreach (EcsChunk chunk in _ecs.Query())
        {
            foreach (Entity entity in chunk.Entities)
            {
                if (!kept.Contains(entity)) left.Add(entity);
            }
        }

        Destroy([.. left]);
        if (left.Count > 0)
            Debugging.Info($"Destroyed {left.Count} entities no domain spawned, as {what} takes the world's place.");
    }

    /// <summary>
    /// Reads what <paramref name="open"/> spawns on workers and hands it to the main thread to be taken in; a domain
    /// whose reading fails or is cancelled is handed over as not coming, and closes.
    /// </summary>
    private async Task ReadAndHandOffDomainAsync(OpenDomain open, CancellationToken cancel)
    {
        try
        {
            ReadDomain read = await ReadPrefabsAsync(open, cancel).ConfigureAwait(false);
            _handOffsToMainThread.Writer.TryWrite(new HandOffToMainThread(open, Read: read));
        }
        catch
        {
            _handOffsToMainThread.Writer.TryWrite(new HandOffToMainThread(open));

            throw;
        }
    }

    /// <summary>
    /// Every prefab in <paramref name="open"/>'s folder, each read on a worker, with the player when it replaces the
    /// world, and the models they draw loaded, held by the domain while it is open so whatever draws them has them the
    /// frame they spawn. The reads are the first half of its progress; the gems' taking it in is the second
    /// (<see cref="AskPreloaders"/>). On a worker.
    /// </summary>
    private async Task<ReadDomain> ReadPrefabsAsync(OpenDomain open, CancellationToken cancel)
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
        if (open.IsReplacing && _app.Project.Player != Handle<Prefab>.None)
        {
            Result<Prefab> loaded = await _assets.LoadAsync(_app.Project.Player, cancel).ConfigureAwait(false);
            if (loaded.Ok) player = loaded.Value;
            else Debugging.Error($"The project's player is not spawned: {loaded.Error}");
        }

        ReadDomain done = new(placements, prefabs, player, []);
        List<Prefab> spawning = [];
        AddPrefabsToSpawn(done, spawning);
        HashSet<Handle<Model>> drawn = [];
        foreach (Prefab prefab in spawning)
        {
            AddDrawnModels(prefab.Parts, drawn);
        }

        List<Task<Result<Model>>> loading = [];
        foreach (Handle<Model> model in drawn)
        {
            loading.Add(_assets.LoadAsync(model, cancel).AsTask());
        }

        foreach (Result<Model> model in await Task.WhenAll(loading).ConfigureAwait(false))
        {
            if (model.Ok) done.Models.Add(model.Value);
        }

        open.Progress?.Report(0.5f);

        return done;

        async Task<Result<Prefab>> ReadOneAsync(Handle<Prefab> file)
        {
            Result<Prefab> prefab = await _assets.LoadAsync(file, cancel).ConfigureAwait(false);
            open.Progress?.Report(0.5f * Interlocked.Increment(ref read) / reading.Length);

            return prefab;
        }
    }

    /// <summary>
    /// Takes in what workers finished since the last frame: a domain read joins what the gems take in, unless it was
    /// closed meanwhile; a prefab read for a placement is spawned; and a domain not coming, its reading failed or its
    /// open cancelled before it was done, is closed.
    /// </summary>
    private void TakeInHandOffs()
    {
        while (_handOffsToMainThread.Reader.TryRead(out HandOffToMainThread handOff))
        {
            OpenDomain open = handOff.OpenDomain;
            if (handOff.PlacedPrefab is { } placed)
            {
                SpawnReadPrefab(open, placed, handOff.Prefab);
            }
            else if (handOff.Read is { } read)
            {
                if (open.Stage != Stage.Closed) open.Read = read;
            }
            else if (!open.Opened.Task.IsCompleted)
            {
                Close(open);
            }
        }
    }

    /// <summary>
    /// Asks every gem how far it is with what the world streamed in as they last saw it (<see cref="Stream"/>): the
    /// mean is the second half of the progress of every domain they were asked about, and once every gem has all of it,
    /// each spawns (<see cref="FinishTakingIn"/>). While a world replacing the old one is readied and all there, they
    /// are asked whether it is ready, and it is shown once every one says so (<see cref="ShowWorld"/>).
    /// </summary>
    private void AskPreloaders()
    {
        List<OpenDomain> asked = GetDomainsAsked();
        if (asked.Count > 0)
        {
            IReadOnlyList<IPreloader> preloaders = _world.Preloaders;
            float sum = 0;
            bool isIn = true;
            foreach (IPreloader preloader in preloaders)
            {
                float progress = AskPreloader(preloader, static (gem, stream) => gem.GetProgress(stream), 1f);
                sum += Math.Clamp(progress, 0, 1);
                isIn &= progress >= 1;
            }

            float mean = preloaders.Count == 0 ? 1 : sum / preloaders.Count;
            foreach (OpenDomain open in asked)
            {
                open.Progress?.Report(0.5f + (0.5f * mean));
                if (isIn) FinishTakingIn(open);
            }
        }

        if (Stream.State != StreamState.Readying || _savedDomainsToJoin > 0) return;

        // Only once everything replacing the world, or put back in its place, has spawned.
        if (_domains.Exists(static open => (open.IsReplacing || open.IsRestoring) && open.Stage < Stage.Spawned))
            return;

        foreach (IPreloader preloader in _world.Preloaders)
        {
            if (!AskPreloader(preloader, static (gem, stream) => gem.IsReady(stream), true)) return;
        }

        ShowWorld();
    }

    /// <summary>
    /// <paramref name="preloader"/>'s answer to <paramref name="ask"/> about the world's stream; one that throws is an
    /// error, said once, and the world goes on without it from then on, as though it had answered
    /// <paramref name="otherwise"/>.
    /// </summary>
    private T AskPreloader<T>(IPreloader preloader, Func<IPreloader, WorldStream, T> ask, T otherwise)
    {
        try
        {
            return ask(preloader, Stream);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Debugging.Error($"Gem {preloader.GetType().Name} threw as it was asked how far it is with what the world "
                + $"streams in, so the world goes on without it: {ex}");
            List<IPreloader> left = [.. _world.Preloaders];
            left.Remove(preloader);
            _world.Preloaders = left;

            return otherwise;
        }
    }

    /// <summary>
    /// The domains the gems were asked about as they last saw the world's stream: those opening, and the loading domain
    /// being taken in to be kept.
    /// </summary>
    private List<OpenDomain> GetDomainsAsked()
    {
        List<OpenDomain> asked = [];
        foreach (OpenDomain open in _domains)
        {
            if (open.IsInLastStream) asked.Add(open);
        }

        if (_keptLoadingDomain?.BeingTakenIn is { IsInLastStream: true } reading) asked.Add(reading);

        return asked;
    }

    /// <summary>
    /// Every gem has what <paramref name="open"/> spawns: the loading domain is kept, and shown while the world loads
    /// with none shown; any other domain arrives (<see cref="SpawnDomain"/>).
    /// </summary>
    private void FinishTakingIn(OpenDomain open)
    {
        if (open.Read is not { } read) return;

        open.Read = null;
        open.IsInLastStream = false;
        if (!open.IsLoadingDomain)
        {
            SpawnDomain(open, read);

            return;
        }

        if (_keptLoadingDomain is not { } keeping || keeping.BeingTakenIn != open) return;

        keeping.BeingTakenIn = null;
        keeping.Read = new ReadLoadingDomain(open.Handle, open.Path, open.Domain, read);
        if (_streamState == StreamState.Loading && _shownLoadingDomain is null) ShowKeptLoadingDomain(keeping.Read);
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
    /// What <paramref name="open"/> spawns, <paramref name="read"/>, is in: spawned now, all of it as one spawn, but
    /// what a domain put back kept (<see cref="Reopen"/>), which only takes its prefab as it is now; the player at its
    /// spawn when it replaces the world. A domain that replaces the world takes the loading domain's place in this
    /// frame: the loading domain closes, its last image left on the window, the world empties for it
    /// (<see cref="EmptyWorld"/>) before it spawns, out of sight, and it is readied
    /// (<see cref="StreamState.Readying"/>); any other is done.
    /// </summary>
    private void SpawnDomain(OpenDomain open, ReadDomain read)
    {
        if (open.IsReplacing)
        {
            if (_shownLoadingDomain is { } loading) Close(loading);

            EmptyWorld(open.Path, keepsGlobals: true);
            EnterStreamState(StreamState.Readying);
        }

        // Each prefab spawns, but one a domain put back already has entities for, which only takes its prefab as it is
        // now.
        int entities = 0;
        for (int i = 0; i < read.Placements.Count; i++)
        {
            Placement placement = read.Placements[i];
            if (!open.Prefabs.TryGetValue(placement.File, out PlacedPrefab? placed))
            {
                placed = new PlacedPrefab(placement);
                open.Prefabs[placement.File] = placed;
            }

            if (read.Prefabs[i] is { Ok: true, Value: { } prefab })
            {
                if (placed.Entities is null) RespawnPrefab(placed, prefab);
                else placed.Prefab = prefab;

                entities += placed.Entities?.Length ?? 0;
            }
            else
            {
                Debugging.Error($"{_assets.GetPath(placement.File) ?? "A prefab"} of {open.Path} is left out: "
                    + $"{read.Prefabs[i].Error}");
            }
        }

        open.Models = read.Models;
        if (read.Player is { } player)
        {
            open.PlayerPrefab = player;
            open.Player = _world.Spawn(player, GetPlayerSpawn(open));
        }

        open.Stage = Stage.Spawned;
        if (!open.IsLoadingDomain)
            Debugging.Info($"Opened {open.Path}: {entities} entities in {open.Prefabs.Count} prefabs.");

        if (!open.IsReplacing) open.Opened.TrySetResult(Result.Success(open.Domain));
    }

    /// <summary>
    /// Shows the world from this frame, every gem having readied it: each domain that replaced it, and the saved world
    /// put back, if one was, are shown, and the world runs from the next frame (<see cref="StreamState.Shown"/>).
    /// </summary>
    private void ShowWorld()
    {
        foreach (OpenDomain open in _domains)
        {
            if (!open.IsReplacing || open.Stage == Stage.Shown) continue;

            open.Stage = Stage.Shown;
            Debugging.Info($"{open.Path} is shown, every gem having readied it.");
            open.Opened.TrySetResult(Result.Success(open.Domain));
            EndReplacement();
        }

        if (_savedDomainsToJoin is null) return;

        _savedDomainsToJoin = null;
        Debugging.Info("The world put back is shown, every gem having readied it.");
        EndReplacement();
    }

    /// <summary>
    /// Spawns <paramref name="prefab"/> where <paramref name="placed"/> is, in place of what it spawned before.
    /// </summary>
    private void RespawnPrefab(PlacedPrefab placed, Prefab prefab)
    {
        Destroy(placed.Entities);
        placed.Prefab = prefab;
        placed.Entities = _world.Spawn(prefab, new Transform { Position = placed.Placement.Corner });
    }

    /// <summary>
    /// The ids of those of <paramref name="entities"/> that are alive, for a save.
    /// </summary>
    private int[] GetAliveIds(Entity[] entities)
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
    /// spawned nothing, <paramref name="kept"/> null, opens again from the start. Either way it has joined the world
    /// put back once its file is in, or will not be.
    /// </summary>
    private async Task RestoreAsync(DomainState saved, Dictionary<Handle<Prefab>, Entity[]>? kept, Entity[]? player)
    {
        Result<Domain> file = await _assets.LoadAsync(saved.Domain, _app.Quitting).ConfigureAwait(false);
        if (!file.Ok)
        {
            Debugging.Error($"{_assets.GetPath(saved.Domain) ?? "A domain"} is not restored: {file.Error}");
            await _threads.PostAsync(EngineThread.Main, () => _savedDomainsToJoin--, _app.Quitting)
                .ConfigureAwait(false);

            return;
        }

        OpenDomain open = new(
            saved.Domain,
            _assets.GetPath(saved.Domain) ?? "A domain",
            file.Value,
            saved.Mode,
            isLoadingDomain: false,
            isReplacing: kept is null && saved.Mode == DomainOpenMode.Replace)
        {
            IsRestoring = true
        };
        bool isOpening = await _threads.PostAsync(
            EngineThread.Main,
            () =>
            {
                _savedDomainsToJoin--;

                if (kept is null) return StartOpeningDomain(open) is null;

                Reopen(open, kept, player);

                return true;
            },
            _app.Quitting).ConfigureAwait(false);

        if (isOpening) await ReadAndHandOffDomainAsync(open, _app.Quitting).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens <paramref name="open"/> around what it had spawned when the world was saved: <paramref name="kept"/>, the
    /// entities made for each of its prefabs', by file, and <paramref name="player"/>, the player's. Each prefab now in
    /// its folder takes its entities; one with none, added since, is read and spawned as the domain arrives; the
    /// entities of a prefab gone since are destroyed. Nothing closes and no loading domain is shown: the world is not
    /// replaced.
    /// </summary>
    private void Reopen(OpenDomain open, Dictionary<Handle<Prefab>, Entity[]> kept, Entity[]? player)
    {
        _domains.Add(open);
        open.Player = player;
        foreach (Placement placement in GetPlacements(open.Handle, open.Domain))
        {
            PlacedPrefab placed = new(placement);
            if (kept.Remove(placement.File, out Entity[]? entities)) placed.Entities = entities;

            open.Prefabs[placement.File] = placed;
        }

        foreach (Entity[] gone in kept.Values)
        {
            Destroy(gone);
        }
    }

    /// <summary>
    /// Takes <paramref name="open"/> out of the world, destroying what it spawned and its player; one not done yet ends
    /// with why not, and one that was to replace the world is no longer waited for.
    /// </summary>
    private void Close(OpenDomain open)
    {
        if (open.Stage == Stage.Closed) return;

        Stage stageBefore = open.Stage;
        open.Stage = Stage.Closed;
        open.Read = null;
        open.IsInLastStream = false;
        if (open.IsLoadingDomain) _shownLoadingDomain = null;
        else _domains.Remove(open);

        foreach (PlacedPrefab placed in open.Prefabs.Values)
        {
            Destroy(placed.Entities);
        }

        open.Prefabs.Clear();
        open.Models = [];
        Destroy(open.Player);
        open.Opened.TrySetResult(Result.Failure<Domain>(stageBefore >= Stage.Spawned
            ? "It was closed before it was shown."
            : "It was closed before it was there."));

        // One closed before it replaced the world is not coming: the world waits for it no more.
        if (stageBefore != Stage.Shown && open.IsReplacing) EndReplacement();
    }

    /// <summary>
    /// Shows the loading domain as a domain begins to replace the world: the project's, or the engine's. Kept, it
    /// spawns now, whole, so it is drawn with all it draws from the frame the open domains close. The first time it is
    /// read and taken in by every gem that needs time to, then kept for the run, and shown once it is unless the domain
    /// replacing the world is there by then (<see cref="FinishTakingIn"/>).
    /// </summary>
    private void ShowLoadingDomain()
    {
        if (_keptLoadingDomain is { Read: { } kept })
        {
            ShowKeptLoadingDomain(kept);

            return;
        }

        if (_keptLoadingDomain is not null) return;

        string path = DefaultLoadingDomainPath + Domain.Extensions[0];
        Handle<Domain> handle = _app.Project.LoadingDomain == Handle<Domain>.None
            ? _assets.Find<Domain>(path)
            : _app.Project.LoadingDomain;
        if (handle == Handle<Domain>.None)
        {
            Debugging.Warn($"There is no loading domain: the project names none, and there is no {path}.");

            return;
        }

        _keptLoadingDomain = new KeptLoadingDomain(handle);
        ReadAndKeepLoadingDomainAsync(_keptLoadingDomain).FireAndForget();
    }

    /// <summary>
    /// Reads the loading domain <paramref name="keeping"/> names, then has the main thread hand it to the gems to take
    /// in and keep (<see cref="FinishTakingIn"/>), unless it was let go meanwhile
    /// (<see cref="DropKeptLoadingDomain"/>). One that does not open is an error, and none is shown until its files
    /// change.
    /// </summary>
    private async Task ReadAndKeepLoadingDomainAsync(KeptLoadingDomain keeping)
    {
        Result<Domain> file = await _assets.LoadAsync(keeping.Handle, _app.Quitting).ConfigureAwait(false);
        if (!file.Ok)
        {
            Debugging.Error($"The loading domain does not open: {file.Error}");

            return;
        }

        OpenDomain reading = new(
            keeping.Handle,
            _assets.GetPath(keeping.Handle) ?? "The loading domain",
            file.Value,
            DomainOpenMode.Additive,
            isLoadingDomain: true,
            isReplacing: false);
        ReadDomain read = await ReadPrefabsAsync(reading, _app.Quitting).ConfigureAwait(false);
        await _threads.PostAsync(
            EngineThread.Main,
            () =>
            {
                if (_keptLoadingDomain != keeping) return;

                reading.Read = read;
                keeping.BeingTakenIn = reading;
            },
            _app.Quitting).ConfigureAwait(false);
    }

    /// <summary>
    /// Spawns the kept loading domain <paramref name="kept"/> now, as it was read.
    /// </summary>
    private void ShowKeptLoadingDomain(ReadLoadingDomain kept)
    {
        _shownLoadingDomain = new OpenDomain(
            kept.Handle,
            kept.Path,
            kept.Domain,
            DomainOpenMode.Additive,
            isLoadingDomain: true,
            isReplacing: false);
        SpawnDomain(_shownLoadingDomain, kept.Read);
    }

    /// <summary>
    /// Lets the kept loading domain go, as its files changed, so the next domain to replace the world reads and takes
    /// it in again, and the gems no longer keep what it needs; one being read is dropped once it is. One shown stays as
    /// it is until it closes.
    /// </summary>
    private void DropKeptLoadingDomain() => _keptLoadingDomain = null;

    /// <summary>
    /// A domain that was replacing the world is shown, or is not coming, or the saved world put back is shown: once
    /// none is left, the loading domain closes if it is still shown, and the world is shown from this frame, running
    /// from the next.
    /// </summary>
    private void EndReplacement()
    {
        if (--_replacementsUnderWay > 0) return;

        EnterStreamState(StreamState.Shown);
        if (_shownLoadingDomain is { } loading) Close(loading);
    }

    /// <summary>
    /// Writes down what streams into the world as this frame begins: where it is, from which frame, what its domains
    /// spawn and are about to, and what of that the gems still take in, a new version of those only when they changed.
    /// Told when its state or its lists changed. The domains taken in are the ones the gems are asked about next
    /// frame.
    /// </summary>
    private void WriteDownStream()
    {
        _framesInStreamState++;
        _gatheredPrefabs.Clear();
        _gatheredTakingIn.Clear();
        foreach (OpenDomain open in _domains)
        {
            open.IsInLastStream = open.Read is not null;
            GatherPrefabs(open);
        }

        if (_keptLoadingDomain?.Read is { } kept) AddPrefabsToSpawn(kept.Read, _gatheredPrefabs);

        if (_keptLoadingDomain?.BeingTakenIn is { } reading)
        {
            reading.IsInLastStream = reading.Read is not null;
            GatherPrefabs(reading);
        }

        bool isChanged = !HasSamePrefabs(_gatheredPrefabs, Stream.Prefabs)
            || !HasSamePrefabs(_gatheredTakingIn, Stream.TakingIn);
        WorldStream stream = isChanged
            ? new WorldStream(
                _streamState,
                _framesInStreamState,
                [.. _gatheredPrefabs],
                [.. _gatheredTakingIn],
                Stream.Version + 1)
            : Stream with { State = _streamState, Frame = _framesInStreamState };
        if (isChanged || stream.State != Stream.State) _events.Publish(stream);

        Stream = stream;

        // What a domain spawns, and of that, while it is read and not yet there, what is still taken in.
        void GatherPrefabs(OpenDomain open)
        {
            foreach (PlacedPrefab placed in open.Prefabs.Values)
            {
                if (placed.Prefab is { } prefab) _gatheredPrefabs.Add(prefab);
            }

            if (open.PlayerPrefab is { } player) _gatheredPrefabs.Add(player);

            if (open.Read is not { } read) return;

            AddPrefabsToSpawn(read, _gatheredPrefabs);
            AddPrefabsToSpawn(read, _gatheredTakingIn);
        }
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
    private List<OpenDomain> GetOpenAndLoadingDomains()
    {
        List<OpenDomain> shown = [.. _domains];
        if (_shownLoadingDomain is { } loading) shown.Add(loading);

        return shown;
    }

    /// <summary>
    /// A domain's file was read again: what it is now, which <see cref="FilesChanged"/>, told next, places its cells
    /// by. The kept loading domain's is read again with it the next time it shows.
    /// </summary>
    private void DomainReloaded(AssetReloaded<Domain> reloaded)
    {
        if (_keptLoadingDomain?.Handle == reloaded.Asset.Handle) DropKeptLoadingDomain();

        foreach (OpenDomain open in GetOpenAndLoadingDomains())
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
        ReadLoadingDomain? kept = _keptLoadingDomain?.Read;
        if (kept?.Read.Placements.Exists(placement => placement.File == reloaded.Asset.Handle) == true)
            DropKeptLoadingDomain();

        foreach (OpenDomain open in GetOpenAndLoadingDomains())
        {
            if (open.Stage >= Stage.Spawned
                && open.Prefabs.TryGetValue(reloaded.Asset.Handle, out PlacedPrefab? placed))
            {
                RespawnPrefab(placed, reloaded.Asset);
            }
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
        if (_keptLoadingDomain?.Read is { } kept
            && !GetPlacements(kept.Handle, kept.Domain).SequenceEqual(kept.Read.Placements))
        {
            DropKeptLoadingDomain();
        }

        foreach (OpenDomain open in GetOpenAndLoadingDomains())
        {
            if (open.Stage < Stage.Spawned) continue;

            // Each prefab in its folder now: one spawned where it still is stays, and any other, come, moved or not
            // loaded, is read again and spawned in its place.
            HashSet<Handle<Prefab>> present = [];
            foreach (Placement placement in GetPlacements(open.Handle, open.Domain))
            {
                present.Add(placement.File);

                if (open.Prefabs.TryGetValue(placement.File, out PlacedPrefab? placed) && placed.Placement == placement
                    && placed.Entities is not null)
                {
                    continue;
                }

                if (placed is not null) Destroy(placed.Entities);

                PlacedPrefab added = new(placement);
                open.Prefabs[placement.File] = added;
                ReadPrefabAsync(open, added).FireAndForget();
            }

            List<Handle<Prefab>> gone = [];
            foreach ((Handle<Prefab> file, PlacedPrefab placed) in open.Prefabs)
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
    private async Task ReadPrefabAsync(OpenDomain open, PlacedPrefab placed)
    {
        Result<Prefab> prefab = await _assets.LoadAsync(placed.Placement.File, _app.Quitting)
            .ConfigureAwait(false);
        _handOffsToMainThread.Writer.TryWrite(new HandOffToMainThread(open, PlacedPrefab: placed, Prefab: prefab));
    }

    /// <summary>
    /// Spawns <paramref name="placed"/>'s prefab, read as <paramref name="prefab"/>, unless its domain closed, or
    /// another took its place, by then.
    /// </summary>
    private void SpawnReadPrefab(OpenDomain open, PlacedPrefab placed, Result<Prefab> prefab)
    {
        if (open.Stage == Stage.Closed
            || !open.Prefabs.TryGetValue(placed.Placement.File, out PlacedPrefab? now)
            || now != placed)
        {
            return;
        }

        if (prefab.Ok)
        {
            RespawnPrefab(placed, prefab.Value);
        }
        else
        {
            Debugging.Error(
                $"{_assets.GetPath(placed.Placement.File) ?? "A prefab"} of {open.Path} is left out: {prefab.Error}");
        }
    }

    /// <summary>
    /// How far a domain has got: opening, its prefabs read and taken in by the gems; spawned; shown, when it replaced
    /// the world and every gem readied it; or closed, whatever stage it was at.
    /// </summary>
    private enum Stage
    {
        Opening,
        Spawned,
        Shown,
        Closed,
    }

    /// <summary>
    /// A prefab of a domain, and where it is spawned: <see cref="Corner"/>, a cell's, or the domain's origin.
    /// </summary>
    private readonly record struct Placement(Handle<Prefab> File, Vector3 Corner, bool IsCell);

    /// <summary>
    /// What a worker finished for <paramref name="OpenDomain"/>: what its opening <paramref name="Read"/>; or
    /// <paramref name="PlacedPrefab"/>'s prefab read as <paramref name="Prefab"/>; or, with neither, that it is not
    /// coming, its reading failed or its open cancelled.
    /// </summary>
    private readonly record struct HandOffToMainThread(
        OpenDomain OpenDomain,
        ReadDomain? Read = null,
        PlacedPrefab? PlacedPrefab = null,
        Result<Prefab> Prefab = default);

    /// <summary>
    /// What a domain's opening read: each of its prefabs, by placement, its player, and the models they draw, held
    /// while it is open.
    /// </summary>
    private sealed record ReadDomain(
        List<Placement> Placements,
        Result<Prefab>[] Prefabs,
        Prefab? Player,
        List<Model> Models);

    /// <summary>
    /// The loading domain as it was read to be kept: its handle, its path for messages, its file, and what was read for
    /// it, which the world's stream names while it is kept (<see cref="KeptLoadingDomain"/>).
    /// </summary>
    private sealed record ReadLoadingDomain(Handle<Domain> Handle, string Path, Domain Domain, ReadDomain Read);

    /// <summary>
    /// The loading domain being read and taken in to be kept (<see cref="BeingTakenIn"/>), or kept
    /// (<see cref="Read"/>). Main thread.
    /// </summary>
    private sealed class KeptLoadingDomain(Handle<Domain> handle)
    {
        internal Handle<Domain> Handle { get; } = handle;

        /// <summary>
        /// It read, being taken in by the gems; none before it is read, once it is kept, or when its reading failed.
        /// </summary>
        internal OpenDomain? BeingTakenIn { get; set; }

        /// <summary>
        /// What was read for it, once every gem has it in; none until then, or when it does not open.
        /// </summary>
        internal ReadLoadingDomain? Read { get; set; }
    }

    /// <summary>
    /// A domain open in the world, or opening.
    /// </summary>
    private sealed class OpenDomain(
        Handle<Domain> handle,
        string path,
        Domain domain,
        DomainOpenMode mode,
        bool isLoadingDomain,
        bool isReplacing)
    {
        internal Handle<Domain> Handle { get; } = handle;

        /// <summary>
        /// Its path, for messages.
        /// </summary>
        internal string Path { get; } = path;

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
        /// It is put back from a saved world (<see cref="Restore"/>), which is readied once it is there.
        /// </summary>
        internal bool IsRestoring { get; init; }

        /// <summary>
        /// Told how far it is, 0 to 1: the reads are the first half, the gems' taking it in the second.
        /// </summary>
        internal IProgress<float>? Progress { get; init; }

        /// <summary>
        /// What its opening read, while the gems take it in; none before it is read, and once it is there.
        /// </summary>
        internal ReadDomain? Read { get; set; }

        /// <summary>
        /// Its read was in the stream the gems last saw, so their answers say whether it is in.
        /// </summary>
        internal bool IsInLastStream { get; set; }

        /// <summary>
        /// How far it has got, from opening to shown, or closed.
        /// </summary>
        internal Stage Stage { get; set; }

        /// <summary>
        /// Ends once it is there, and shown when it replaces the world, with it; or with why not when it is closed
        /// first.
        /// </summary>
        internal TaskCompletionSource<Result<Domain>> Opened { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Its prefabs, by file.
        /// </summary>
        internal Dictionary<Handle<Prefab>, PlacedPrefab> Prefabs { get; } = [];

        /// <summary>
        /// The models its prefabs drew as it opened, held while it is open so whatever draws them keeps them.
        /// </summary>
        internal List<Model> Models { get; set; } = [];

        /// <summary>
        /// The project's player, when it replaced the world, or the one kept as it was put back; and the prefab it was
        /// spawned from, which the world's stream names while it is open.
        /// </summary>
        internal Entity[]? Player { get; set; }

        internal Prefab? PlayerPrefab { get; set; }
    }

    /// <summary>
    /// A prefab of a domain where it is spawned, and what it spawned: none yet, or when it did not load.
    /// </summary>
    private sealed class PlacedPrefab(Placement placement)
    {
        internal Placement Placement { get; } = placement;

        internal Prefab? Prefab { get; set; }

        internal Entity[]? Entities { get; set; }
    }
}
