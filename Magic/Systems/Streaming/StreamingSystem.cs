using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.Events;
using Magic.Interfaces;
using Magic.Mathematics;
using Magic.Services;
using Magic.Utils;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Text.Json;

namespace Magic.Systems.Streaming;

/// <summary>
/// What the streaming system is doing, over every open domain, for the debug display and the logs. Kept is the chunks
/// out of view that are still spawned; StandIns is the chunks spawned as their stand-in (a LOD) for being far; Bytes
/// is what the spawned chunks count for against Budget, both in bytes. Filling is the chunks no camera wants that are
/// on their way to fill the budget; Bootstrapping is whether a domain is still filling behind the loading domain.
/// </summary>
internal readonly record struct StreamingStats(int Domains, int Cameras, int Wanted, int Active, int Loaded, int Loading, int Kept, int StandIns, int Entities, long Bytes, long Budget, int Filling, bool Bootstrapping);

/// <summary>
/// Keeps the ECS filled with the open <see cref="Domain"/>s, which are whatever <see cref="World.Active"/> says
/// this frame: a domain that appears there has its global chunks spawned under <c>World.&lt;Name&gt;.Globals</c>, where
/// they stay, and one that is gone from it has everything of it destroyed. In between, every frame, for every
/// camera of the domain, the chunk cube the camera stands in (active), the cubes within
/// <see cref="Contexts.Settings.StreamingSettings.Radius"/> of it, and the cubes its
/// frustum touches within <see cref="Contexts.Settings.RenderSettings.ViewDist"/> are wanted, of the cubes that have an
/// <c>x_y_z.chunk</c> file beside the domain file: they load off the main thread, nearest first and
/// <see cref="MaxLoads"/> at a time, where their components are read too; the main thread only spawns them, nearest first and
/// <see cref="SpawnBudgetMs"/> worth a frame, under <c>World.&lt;Name&gt;.Chunks.Cx_y_z</c>. A camera under one
/// domain's root wants nothing of another domain; one under none wants of all.
/// <para>
/// Streaming is greedy: once what the cameras want is on its way, the cubes nobody wants are loaded too, nearest
/// first, until the spawned chunks together would be over the Chunk budget of
/// <see cref="Contexts.Settings.AssetSettings"/> (the fill), so whichever way a camera turns or goes the world is
/// there already. When the budget is full, a nearer cube still takes the place of the farthest one kept. A cube that
/// goes out of view stays spawned until the spawned chunks are over the budget: then the cubes out of view are
/// destroyed, the farthest first. A cube in view is never destroyed for the budget; when those alone are over it,
/// that is warned about once. The budgets are shared by all open domains.
/// </para>
/// <para>
/// A cube far from every camera is spawned as a lesser version of its chunk when it has one (<see cref="Assets.Lods{T}"/>:
/// a stand-in, good beyond its threshold in metres times <see cref="Contexts.Settings.RenderSettings.LodBias"/>), which is what lets
/// everything in view be loaded however far it is. A camera crossing the threshold swaps the two, and only once it is
/// <see cref="LodHysteresis"/> past it, so standing on it does not swap back and forth. The one coming in is spawned
/// <see cref="Hidden"/> and stays so until the renderer has registered every one of its entities; then it is shown
/// and the other destroyed in the same frame, so there is never a frame with both drawn or with neither.
/// </para>
/// This is the only code that reads a chunk's
/// entities: a component is set from its JSON by the name it was written under, resolved against every loaded
/// assembly, so gems add components without registering anything; an entity's scripts go to the
/// <see cref="ScriptSystem"/> as they are. Asset and gem changes arrive in
/// <see cref="Frame.Events"/>. It runs in Update.
/// <para>
/// Nothing here waits for a chunk's file. A domain's globals and its cubes are loads that are polled, each stopped
/// when nobody wants it any more. A domain the world opened is <see cref="DomainState.Loading"/> until its
/// globals and every cube its cameras want are spawned: the world is told how much of that is there, each frame
/// it grows, and then that the domain is <see cref="DomainState.Loaded"/>.
/// </para>
/// <para>
/// A domain opened behind the world's <see cref="World.Loading"/> domain is held until then: its root is
/// <see cref="Hidden"/>, so nothing of it is drawn, and its scripts wait (<see cref="ScriptSystem.Release"/>). It is
/// loaded only once the fill is done too and the renderer has every cube, so it appears whole. While one is held,
/// loads and spawning take <see cref="BootstrapLoads"/> and <see cref="BootstrapSpawnMs"/>: there is nothing to
/// keep smooth but a loading screen. Afterwards the cubes a camera comes to want go first; a change of level and
/// the fill share what is left, at most half the loads.
/// </para>
/// </summary>
internal sealed class StreamingSystem : ISystem
{
    /// <summary>
    /// Chunk loads in flight at once, as many as the machine has cores to spare; the rest wait their turn, nearest
    /// first, so the near ones are not queued behind the far.
    /// </summary>
    private static readonly int MaxLoads = Math.Max(2, Environment.ProcessorCount - 2);

    /// <summary>
    /// Milliseconds a frame may spend spawning, kept by spawning as many entities as the last frames show fit in it.
    /// A chunk that takes longer is spawned over several frames, so a big one neither stalls a frame nor streaming.
    /// </summary>
    private const double SpawnBudgetMs = 2;

    /// <summary>
    /// <see cref="MaxLoads"/> while a domain fills behind the loading domain: every core.
    /// </summary>
    private static readonly int BootstrapLoads = Math.Max(2, Environment.ProcessorCount);

    /// <summary>
    /// <see cref="SpawnBudgetMs"/> while a domain fills behind the loading domain.
    /// </summary>
    private const double BootstrapSpawnMs = 12;

    /// <summary>
    /// The fewest entities a frame spawns, however long the last ones took.
    /// </summary>
    private const int SpawnBatch = 32;

    /// <summary>
    /// How far past a LOD's threshold, as a share of it, a camera has to be before a spawned cube changes to or from it.
    /// </summary>
    private const float LodHysteresis = 0.1f;

    /// <summary>
    /// The frustum is built wider than any window so an edge cube is never missed.
    /// </summary>
    private const float StreamingAspect = 2f;

    private readonly IEcs _ecs;
    private readonly Assets _assets;
    private readonly Project _project;
    private readonly ScriptSystem? _scripts;
    private readonly World _world;
    private readonly Threads _threads;
    private readonly IEcsQuery<Camera, WorldTransform> _cameras;

    // One per open domain, in the order they were opened; their roots hang under the one World entity.
    private readonly List<Stream> _streams = [];
    private Handle _root;
    private readonly List<(Handle Owner, Vector3 Eye, Frustum Frustum)> _views = []; // this frame's cameras, each with the domain root it is under, if any
    private readonly List<(int X, int Y, int Z)> _finished = [];
    private readonly List<(int X, int Y, int Z)> _unwanted = [];
    private readonly List<(Stream Stream, (int X, int Y, int Z) Chunk)> _candidates = [];
    private readonly Comparison<(Stream Stream, (int X, int Y, int Z) Chunk)> _nearestFirst; // one delegate, not one per frame
    private Spawning? _spawning; // the cube being spawned, when one frame's budget was not enough for it
    private readonly List<Spawning> _revealing = []; // spawned whole and hidden, taking a spawned cube's place once the renderer has all of them
    private readonly bool _drawn; // there is a renderer to wait for
    private double _spawnMsPerEntity = SpawnBudgetMs / 256; // as the last frames measured it; a guess until then
    private bool _overBudget; // warned about, until the chunks in view fit again
    private bool _bootstrapping; // a domain is filling behind the loading domain
    private bool _fillDone; // nothing more can be loaded to fill the budget, as of this frame
    private long _spawnedBytes; // what the spawned chunks count for, as of the last frame
    private int _spawnedCount;
    private float? _farthestKept; // how far the farthest spawned cube no camera wants is, squared

    // One fill: from the first load begun until nothing is loading or ready any more, for the verbose log.
    private long _fillStarted;
    private int _fillFrames;
    private int _fillChunks;
    private double _fillWorstMs;

    private readonly ChunkReader _reader = new();

    public StreamingSystem(IEcs ecs, Assets assets, Project project, ScriptSystem? scripts, World world, Threads threads, IRendering? rendering)
    {
        _drawn = rendering is not null;
        _ecs = ecs;
        _assets = assets;
        _project = project;
        _scripts = scripts;
        _world = world;
        _threads = threads;
        _cameras = ecs.Query<Camera, WorldTransform>().Build();
        _nearestFirst = (left, right) => DistanceSquared(left.Stream, left.Chunk).CompareTo(DistanceSquared(right.Stream, right.Chunk));
    }

    public void Dispose()
    {
        while (_streams.Count > 0)
            Close(_streams[^1]);

        _cameras.Dispose();
    }

    public StreamingStats Stats { get; private set; }

    /// <summary>
    /// One frame of streaming: this frame's asset and gem events, what the world has open, finished loads, then what the cameras want.
    /// </summary>
    public void Run(in Frame frame)
    {
        foreach (Event change in frame.Events.Span)
            Apply(change);

        Reconcile();
        _bootstrapping = _streams.Exists(stream => stream.Held);
        SpawnReady();

        if (_streams.Count == 0)
        {
            Stats = default;
            return;
        }

        FindCameras();
        foreach (Stream stream in _streams)
            FindWanted(stream);

        LoadNearest();
        (int kept, long bytes, long budget) = UnloadOutOfView();

        int wanted = 0, active = 0, loaded = 0, loading = 0, standIns = 0, entities = 0, filling = 0;
        bool shown = false;
        foreach (Stream stream in _streams)
        {
            wanted += stream.Wanted.Count;
            active += stream.Active.Count;
            loaded += stream.Spawned.Count;
            loading += stream.Loading.Count + stream.Ready.Count + stream.GlobalLoads.Count + (_spawning?.Stream == stream ? 1 : 0) + Revealing(stream);
            shown |= ReportFill(stream);
            foreach ((_, _, int count, _, int level, _) in stream.Spawned.Values)
            {
                entities += count;
                standIns += level > 0 ? 1 : 0;
            }

            foreach ((int X, int Y, int Z) coordinate in stream.Fill)
                filling += stream.Spawned.ContainsKey(coordinate) ? 0 : 1;
        }

        LogFill(loading);

        Stats = new StreamingStats(_streams.Count, _views.Count, wanted, active, loaded, loading, kept, standIns, entities, bytes, budget, filling, _bootstrapping);

        // The world closed the loading domain as the last held one was shown: it goes in this frame, not the next,
        // so the two are never drawn together.
        if (shown)
            Reconcile();
    }

    /// <summary>
    /// The cubes one camera needs, of the <paramref name="cubes"/> there are: the one it stands in (active), every
    /// cube within <paramref name="radius"/> metres of it wherever it looks (so turning never waits on a load), and
    /// every cube within <paramref name="viewDist"/> metres (which may be infinite) that its frustum touches.
    /// <paramref name="frustum"/> is camera-relative, as <see cref="Camera.Frustum"/> gives.
    /// </summary>
    private static void Select(float chunkSize, Vector3 camera, in Frustum frustum, float radius, float viewDist, IEnumerable<(int X, int Y, int Z)> cubes, HashSet<(int X, int Y, int Z)> wanted, HashSet<(int X, int Y, int Z)> active)
    {
        (int X, int Y, int Z) at = Coordinate(camera, chunkSize);
        float near2 = radius * radius; // the cube it stands in is no distance away, so it is in whatever the radius
        float far2 = viewDist * viewDist;
        foreach ((int X, int Y, int Z) cube in cubes)
        {
            Vector3 min = (new Vector3(cube.X, cube.Y, cube.Z) * chunkSize) - camera;
            Aabb box = new(min, min + new Vector3(chunkSize));
            float distance2 = box.DistanceSquared(Vector3.Zero);
            if (distance2 > near2 && (distance2 > far2 || !frustum.Intersects(box)))
                continue;

            wanted.Add(cube);
            if (cube == at)
                active.Add(cube);
        }
    }

    private static (int X, int Y, int Z) Coordinate(Vector3 position, float chunkSize)
    {
        return ((int)MathF.Floor(position.X / chunkSize), (int)MathF.Floor(position.Y / chunkSize), (int)MathF.Floor(position.Z / chunkSize));
    }

    private Stream? Find(ulong domain)
    {
        foreach (Stream stream in _streams)
        {
            if (stream.Handle.Id == domain)
                return stream;
        }

        return null;
    }

    /// <summary>
    /// A changed domain file reopens the domain, a changed global chunk respawns its globals, a changed cube reloads,
    /// a removed file goes; new gems may bring component types.
    /// </summary>
    private void Apply(Event change)
    {
        if (change.Type is EventType.AssetAdded or EventType.AssetRemoved or EventType.AssetMoved)
        {
            foreach (Stream stream in _streams)
                stream.Files = null;
        }

        switch (change.Type)
        {
            case EventType.GemsChanged:
                _reader.Forget();
                foreach (Stream stream in _streams)
                {
                    DropUnspawned(stream);
                    if (stream.GlobalLoads.Count > 0)
                        ReloadGlobals(stream);
                }
                break;
            case EventType.AssetRemoved when Find(change.Id) is { } removed:
                _world.Close(removed.Handle);
                Close(removed);
                break;
            case EventType.AssetModified when Find(change.Id) is { } modified:
                // The world still has it open: it loads again as the file is now.
                Close(modified);
                _world.Set(modified.Handle, DomainState.Loading);
                break;
            case EventType.AssetModified when _streams.Find(open => open.Globals.Exists(chunk => chunk.Id == change.Id)) is { } owner:
                ReloadGlobals(owner);
                break;
            case EventType.AssetRemoved:
            case EventType.AssetMoved:
            case EventType.AssetModified:
                ForgetChunk(change.Id); // one still there and in view is wanted again next frame, so it reloads as it is now
                break;
        }
    }

    /// <summary>
    /// Makes what is open what the world says is open: a domain gone from <see cref="World.Active"/> is closed, and
    /// one that is not here yet gets its roots and its globals begin to load; its cubes stream in from this frame
    /// on. The world loaded its file when it opened it, so that is at hand. A domain whose changed file can no
    /// longer be loaded is closed in the world (logged).
    /// </summary>
    private void Reconcile()
    {
        IReadOnlyList<(Handle<Domain> Domain, DomainState State, float Progress)> active = _world.Active;
        for (int i = active.Count - 1; i >= 0; i--)
        {
            Handle<Domain> handle = active[i].Domain;
            if (Find(handle.Id) is not null)
                continue;

            if (_assets.Load(handle) is { } domain)
                Open(handle, domain);
            else
            {
                Debugging.Log.Error($"Domain {_assets.PathOf(handle.Id)} is closed: it can no longer be loaded.");
                _world.Close(handle);
            }
        }

        for (int i = _streams.Count - 1; i >= 0; i--)
        {
            if (!IsOpen(active, _streams[i].Handle))
                Close(_streams[i]);
        }

        // In the order the world opened them.
        if (_streams.Count > 1)
            _streams.Sort((left, right) => IndexOf(active, left.Handle).CompareTo(IndexOf(active, right.Handle)));
    }

    private static bool IsOpen(IReadOnlyList<(Handle<Domain> Domain, DomainState State, float Progress)> active, Handle<Domain> domain)
    {
        return IndexOf(active, domain) >= 0;
    }

    private static int IndexOf(IReadOnlyList<(Handle<Domain> Domain, DomainState State, float Progress)> active, Handle<Domain> domain)
    {
        for (int i = 0; i < active.Count; i++)
        {
            if (active[i].Domain == domain)
                return i;
        }

        return -1;
    }

    /// <summary>
    /// Gives the domain a root of its own and begins loading its globals. One opened behind the world's loading
    /// domain is held: hidden, root and all, until it is loaded.
    /// </summary>
    private void Open(Handle<Domain> handle, Domain domain)
    {
        if (!_root.IsValid || !_ecs.IsAlive(_root))
            _root = _ecs.Create("World");

        Handle root = _ecs.Create(domain.Name, _root);
        Stream stream = new(handle, domain, root, _ecs.Create("Globals", root), _ecs.Create("Chunks", root))
        {
            Held = handle != _world.Loading && _world.StateOf(_world.Loading) != DomainState.Closed,
        };
        _streams.Add(stream);
        if (stream.Held)
            _ecs.Add<Hidden>(root);

        LoadGlobals(stream);
        Debugging.Log.Info($"Domain {domain.Name} opened: {stream.Globals.Count} global chunk(s), chunk size {domain.ChunkSize} m.");
    }

    private void Close(Stream stream)
    {
        DropUnspawned(stream);
        foreach ((_, _, CancellationTokenSource cancel) in stream.GlobalLoads)
            Abandon(cancel);

        stream.GlobalLoads.Clear();
        _streams.Remove(stream);
        if (_ecs.IsAlive(stream.Root))
            _ecs.Destroy(stream.Root);

        Debugging.Log.Info($"Domain {stream.Domain.Name} closed.");
    }

    /// <summary>
    /// Begins the load of every global chunk of the domain; they spawn, in this order, as they arrive.
    /// </summary>
    private void LoadGlobals(Stream stream)
    {
        foreach (Handle<Chunk> handle in _assets.FindAll<Chunk>(stream.Domain.Folder, ".chunk"))
        {
            string name = Path.GetFileNameWithoutExtension(_assets.PathOf(handle.Id) ?? "");
            if (Chunk.ParseCoordinate(name) is not null)
                continue;

            stream.Globals.Add(handle);
#pragma warning disable CA2000 // GlobalLoads owns it: Abandon, or the load's end, disposes it
            CancellationTokenSource cancel = new();
#pragma warning restore CA2000
            stream.GlobalLoads.Add((handle, LoadGlobalAsync(handle, cancel.Token), cancel));
        }
    }

    private void ReloadGlobals(Stream stream)
    {
        foreach (Handle child in _ecs.GetChildren(stream.GlobalsRoot))
            _ecs.Destroy(child);

        foreach ((_, _, CancellationTokenSource cancel) in stream.GlobalLoads)
            Abandon(cancel);

        stream.GlobalLoads.Clear();
        stream.Globals.Clear();
        LoadGlobals(stream);
    }

    /// <summary>
    /// Stops a load nobody wants any more.
    /// </summary>
    private static void Abandon(CancellationTokenSource cancel)
    {
        cancel.Cancel();
        cancel.Dispose();
    }

    /// <summary>
    /// Tells the world how much of a loading domain is there, of its globals and the cubes its cameras want that can
    /// be loaded, and that it is loaded once it has caught up: all of them are spawned and nothing of it is loading
    /// or waiting to spawn. A held domain has caught up only when the budget is filled as well; it is then shown and
    /// its scripts are let go, which is what true says.
    /// </summary>
    private bool ReportFill(Stream stream)
    {
        if (!stream.Filling)
            return false;

        int total = stream.Globals.Count;
        int done = total - stream.GlobalLoads.Count;
        foreach ((int X, int Y, int Z) coordinate in stream.Wanted)
        {
            bool spawned = stream.Spawned.ContainsKey(coordinate);
            if (!spawned && (stream.Files is null || !stream.Files.TryGetValue(coordinate, out Handle<Chunk> handle) || _assets.HasFailed(handle)))
                continue;

            total++;
            done += spawned ? 1 : 0;
        }

        if (done == total && stream.Loading.Count == 0 && stream.Ready.Count == 0 && _spawning?.Stream != stream && Revealing(stream) == 0 && (!stream.Held || _fillDone))
        {
            bool held = stream.Held;
            stream.Filling = false;
            stream.Held = false;
            if (held)
            {
                _ecs.Remove<Hidden>(stream.Root);
                _scripts?.Release();
            }

            _world.Set(stream.Handle, DomainState.Loaded);
            return held;
        }

        // The cameras may come to want more while it fills; what was told is never taken back.
        float progress = (float)done / Math.Max(1, total);
        if (stream.Held && !_fillDone)
        {
            // As far as the fill is: the share of the domain's cubes that are there, or of the budget that is taken.
            long budget = _assets.BudgetOf<Chunk>();
            float cubes = stream.Files is { Count: > 0 } files ? (float)stream.Spawned.Count / files.Count : 1f;
            float taken = budget > 0 ? (float)_spawnedBytes / budget : 1f;
            progress = MathF.Min(progress, MathF.Max(cubes, taken));
        }

        if (progress <= stream.Progress || progress >= 1f)
            return false;

        stream.Progress = progress;
        _world.Report(stream.Handle, progress);

        return false;
    }

    /// <summary>
    /// Drops every chunk of the stream not spawned yet. When gems change, its components may be of a gem that just
    /// went: the cubes are still wanted, so they load again next frame with the types there are now.
    /// </summary>
    private void DropUnspawned(Stream stream)
    {
        if (_spawning?.Stream == stream)
            DropSpawning();

        for (int i = _revealing.Count - 1; i >= 0; i--)
        {
            if (_revealing[i].Stream == stream)
                DropRevealing(i);
        }

        foreach ((_, _, CancellationTokenSource cancel) in stream.Loading.Values)
            Abandon(cancel);

        stream.Loading.Clear();
        stream.Ready.Clear();
        stream.Fill.Clear();
    }

    /// <summary>
    /// Drops a streamed chunk by asset id, loaded, ready or loading; a no-op for other ids.
    /// </summary>
    private void ForgetChunk(ulong id)
    {
        foreach (Stream stream in _streams)
        {
            foreach (((int X, int Y, int Z) coordinate, (Handle<Chunk> asset, _, _, _, _, _)) in stream.Spawned)
            {
                if (asset.Id != id)
                    continue;

                Unload(stream, coordinate);
                stream.Sizes.Remove(coordinate); // it may count for something else now
                return;
            }

            foreach (((int X, int Y, int Z) coordinate, (Handle<Chunk> asset, _)) in stream.Ready)
            {
                if (asset.Id != id)
                    continue;

                Unload(stream, coordinate);
                return;
            }

            foreach (((int X, int Y, int Z) coordinate, (Handle<Chunk> asset, _, _)) in stream.Loading)
            {
                if (asset.Id != id)
                    continue;

                Unload(stream, coordinate);
                return;
            }
        }
    }

    /// <summary>
    /// This frame's cameras: where each is, what it sees, and which domain's root it is under.
    /// </summary>
    private void FindCameras()
    {
        _views.Clear();
        _cameras.Run((ReadOnlySpan<Handle> entities, Span<Camera> cameras, Span<WorldTransform> worlds) =>
        {
            for (int i = 0; i < cameras.Length; i++)
                _views.Add((DomainRootOf(entities[i]), worlds[i].Value.Translation, cameras[i].Frustum(worlds[i].Value, StreamingAspect)));
        });
    }

    /// <summary>
    /// The root of the domain the entity is under; none for one that is under no domain.
    /// </summary>
    private Handle DomainRootOf(Handle entity)
    {
        for (Handle parent = _ecs.GetParent(entity); parent.IsValid; parent = _ecs.GetParent(entity))
        {
            if (parent == _root)
                return entity;

            entity = parent;
        }

        return Handle.None;
    }

    /// <summary>
    /// What the cameras want of one domain: its own cameras and those under no domain. Another domain's camera (the
    /// loading domain's, say) wants nothing of it.
    /// </summary>
    private void FindWanted(Stream stream)
    {
        stream.Wanted.Clear();
        stream.Active.Clear();
        stream.Cameras.Clear();

        float chunkSize = stream.Domain.ChunkSize;
        float radius = MathF.Max(0f, _project.Settings.Streaming.Radius);
        float viewDist = MathF.Max(0f, _project.Settings.Render.ViewDist);
        Dictionary<(int X, int Y, int Z), Handle<Chunk>> files = stream.Files ??= Files(stream);

        foreach ((Handle owner, Vector3 eye, Frustum frustum) in _views)
        {
            if (owner.IsValid && owner != stream.Root)
                continue;

            Select(chunkSize, eye, frustum, radius, viewDist, files.Keys, stream.Wanted, stream.Active);
            stream.Cameras.Add(eye);
        }
    }

    /// <summary>
    /// The domain's cubes that have a chunk file, by coordinate: the only ones there are to want.
    /// </summary>
    private Dictionary<(int X, int Y, int Z), Handle<Chunk>> Files(Stream stream)
    {
        Dictionary<(int X, int Y, int Z), Handle<Chunk>> files = [];
        foreach (Handle<Chunk> handle in _assets.FindAll<Chunk>(stream.Domain.Folder, ".chunk"))
        {
            if (Chunk.ParseCoordinate(Path.GetFileNameWithoutExtension(_assets.PathOf(handle.Id) ?? "")) is { } coordinate)
                files[coordinate] = handle;
        }

        return files;
    }

    /// <summary>
    /// Starts loads, of any domain, nearest first, while fewer than <see cref="MaxLoads"/> are in flight: the cubes
    /// the cameras want that are not there at all; then, with what is left and no more than half of them once
    /// nothing is held, the spawned cubes that are at another level of detail than their distance now asks for, and
    /// the fill (<see cref="LoadFill"/>). So a cube that comes into view is never queued behind what can wait.
    /// </summary>
    private void LoadNearest()
    {
        int limit = _bootstrapping ? BootstrapLoads : MaxLoads;
        int loading = 0, waiting = 0; // waiting: the loads in flight that could wait
        foreach (Stream stream in _streams)
        {
            loading += stream.Loading.Count;
            foreach ((int X, int Y, int Z) coordinate in stream.Loading.Keys)
                waiting += stream.Spawned.ContainsKey(coordinate) || stream.Fill.Contains(coordinate) ? 1 : 0;
        }

        _fillDone = false;
        if (loading >= limit)
            return;

        _candidates.Clear();
        foreach (Stream stream in _streams)
        {
            foreach ((int X, int Y, int Z) coordinate in stream.Wanted)
            {
                if (!stream.Spawned.ContainsKey(coordinate) && !IsInHand(stream, coordinate))
                    _candidates.Add((stream, coordinate));
            }
        }

        _candidates.Sort(_nearestFirst);
        for (int i = 0; i < _candidates.Count && loading < limit; i++)
        {
            if (StartLoad(_candidates[i].Stream, _candidates[i].Chunk))
                loading++;
        }

        // A cube no camera wants changes level too, so what is behind a camera is as it should be when it turns.
        int most = _bootstrapping ? limit : Math.Max(1, MaxLoads / 2);
        _candidates.Clear();
        foreach (Stream stream in _streams)
        {
            if (stream.Cameras.Count == 0)
                continue;

            foreach (((int X, int Y, int Z) coordinate, (Handle<Chunk> Asset, Handle Root, int Entities, long Bytes, int Level, (float Threshold, Handle<Chunk> Asset)[] Lods) spawned) in stream.Spawned)
            {
                if (!IsInHand(stream, coordinate) && Level(spawned.Lods, Distance(stream, coordinate), spawned.Level) != spawned.Level)
                    _candidates.Add((stream, coordinate));
            }
        }

        _candidates.Sort(_nearestFirst);
        for (int i = 0; i < _candidates.Count && loading < limit && waiting < most; i++)
        {
            (Stream stream, (int X, int Y, int Z) coordinate) = _candidates[i];
            if (!StartLoad(stream, coordinate))
                continue;

            loading++;
            waiting++;
            if (!stream.Wanted.Contains(coordinate))
                stream.Fill.Add(coordinate);
        }

        LoadFill(limit, most, loading, waiting);
        _candidates.Clear();
    }

    /// <summary>
    /// The fill: starts the nearest cubes no camera wants, of any domain that has a camera, while the spawned chunks
    /// and those on their way stay within the Chunk budget. A cube counts for what it did the last time it was
    /// spawned, or for what the spawned ones do on average. Nearest first, so it ends at the first that has no room;
    /// that one is still started when it is more than a chunk nearer than the farthest cube kept, which then goes
    /// for the budget (<see cref="UnloadOutOfView"/>): the fill follows the cameras. A budget of 0 fills nothing.
    /// Notes whether the fill is done: nothing more can be started.
    /// </summary>
    private void LoadFill(int limit, int most, int loading, int waiting)
    {
        _fillDone = true;
        long budget = _assets.BudgetOf<Chunk>();
        if (budget == 0)
            return;

        long average = _spawnedCount > 0 ? _spawnedBytes / _spawnedCount : 0;
        long bytes = _spawnedBytes;
        int pending = 0;
        _candidates.Clear();
        foreach (Stream stream in _streams)
        {
            foreach ((int X, int Y, int Z) coordinate in stream.Fill)
            {
                if (stream.Spawned.ContainsKey(coordinate))
                    continue;

                bytes += stream.Sizes.GetValueOrDefault(coordinate, average);
                pending++;
            }

            if (stream.Cameras.Count == 0 || stream.Files is null)
                continue;

            foreach (((int X, int Y, int Z) coordinate, Handle<Chunk> handle) in stream.Files)
            {
                if (!stream.Wanted.Contains(coordinate) && !stream.Spawned.ContainsKey(coordinate) && !IsInHand(stream, coordinate) && !_assets.HasFailed(handle))
                    _candidates.Add((stream, coordinate));
            }
        }

        _candidates.Sort(_nearestFirst);
        foreach ((Stream stream, (int X, int Y, int Z) coordinate) in _candidates)
        {
            long size = stream.Sizes.GetValueOrDefault(coordinate, average);
            bool fits = bytes + size <= budget;
            bool replaces = !fits && pending == 0 && _farthestKept is { } farthest
                && MathF.Sqrt(farthest) - MathF.Sqrt(DistanceSquared(stream, coordinate)) > stream.Domain.ChunkSize;
            if (!fits && !replaces)
                return;

            _fillDone = false;
            if (loading >= limit || waiting >= most)
                return;

            if (!StartLoad(stream, coordinate))
                continue;

            stream.Fill.Add(coordinate);
            bytes += size;
            loading++;
            waiting++;
            pending++;
            if (replaces)
                return;
        }
    }

    /// <summary>
    /// Whether the cube is on its way: loading, ready to spawn, being spawned or waiting to be shown.
    /// </summary>
    private bool IsInHand(Stream stream, (int X, int Y, int Z) coordinate)
    {
        return stream.Loading.ContainsKey(coordinate) || stream.Ready.ContainsKey(coordinate) || IsSpawning(stream, coordinate);
    }

    /// <summary>
    /// Whether a load began: not for a cube whose file went this frame, or one that failed before. A cube spawned
    /// already is loaded at the level it should change to; a new one at whatever its distance asks for.
    /// </summary>
    private bool StartLoad(Stream stream, (int X, int Y, int Z) coordinate)
    {
        if (stream.Files is null || !stream.Files.TryGetValue(coordinate, out Handle<Chunk> handle) || _assets.HasFailed(handle))
            return false;

        float distance = Distance(stream, coordinate);
        int? level = stream.Spawned.TryGetValue(coordinate, out (Handle<Chunk> Asset, Handle Root, int Entities, long Bytes, int Level, (float Threshold, Handle<Chunk> Asset)[] Lods) spawned)
            ? Level(spawned.Lods, distance, spawned.Level)
            : null;

        CancellationTokenSource cancel = new();
        stream.Loading[coordinate] = (handle, LoadAsync(handle, level, distance, cancel.Token), cancel);

        return true;
    }

    /// <summary>
    /// Metres from the nearest of its domain's cameras to the cube; 0 for one a camera is in.
    /// </summary>
    private float Distance(Stream stream, (int X, int Y, int Z) coordinate)
    {
        Vector3 min = new Vector3(coordinate.X, coordinate.Y, coordinate.Z) * stream.Domain.ChunkSize;
        Aabb box = new(min, min + new Vector3(stream.Domain.ChunkSize));
        float nearest = float.MaxValue;
        foreach (Vector3 eye in stream.Cameras)
            nearest = MathF.Min(nearest, box.DistanceSquared(eye));

        return MathF.Sqrt(nearest);
    }

    /// <summary>
    /// The level of detail a cube that far away should be at: 0 is the chunk itself, n the n-th of
    /// <paramref name="lods"/>, which has the highest threshold first. One at <paramref name="current"/> stays there
    /// until the distance is <see cref="LodHysteresis"/> clear of the threshold; -1 is a cube not spawned yet.
    /// </summary>
    private int Level((float Threshold, Handle<Chunk> Asset)[] lods, float distance, int current)
    {
        int level = Pick(distance);
        return level == current || Pick(distance * (1f - LodHysteresis)) == current || Pick(distance * (1f + LodHysteresis)) == current ? current : level;

        int Pick(float metres)
        {
            float bias = MathF.Max(0.01f, _project.Settings.Render.LodBias);
            for (int i = 0; i < lods.Length; i++)
            {
                if (metres > lods[i].Threshold * bias)
                    return i + 1;
            }

            return 0;
        }
    }

    /// <summary>
    /// The chunk, read and prepared off the main thread, done only once every asset its components have a handle to has been
    /// loaded with what it names in turn. They land in the asset manager's pools, so the renderer's own loads in
    /// the frame the chunk spawns find them there instead of reading and importing on the main thread; one already
    /// pooled costs nothing. With them comes what the chunk counts for in its pool, which is what it counts for spawned.
    /// What is read is the chunk at <paramref name="level"/>, or, when that is null, at the level
    /// <paramref name="distance"/> asks for: the chunk's own file, or one of its lesser versions, which are found
    /// here (and made here, the first time) so the main thread never waits for them. A lesser version that cannot be
    /// read is the chunk itself. Cancelling <paramref name="cancel"/> stops it.
    /// </summary>
    private async Task<Loaded?> LoadAsync(Handle<Chunk> handle, int? level, float distance, CancellationToken cancel)
    {
        (float Threshold, Handle<Chunk> Asset)[] lods = await _assets.LodsAsync(handle, cancel: cancel).ConfigureAwait(false);
        int picked = Math.Min(level ?? Level(lods, distance, -1), lods.Length);
        Handle<Chunk> shown = picked == 0 ? handle : lods[picked - 1].Asset;
        Chunk? chunk = await _assets.LoadAsync(shown, cancel: cancel).ConfigureAwait(false);
        if (chunk is null && picked != 0)
        {
            picked = 0;
            shown = handle;
            chunk = await _assets.LoadAsync(handle, cancel: cancel).ConfigureAwait(false);
        }

        if (chunk is null)
            return null;

        return await PrepareAsync(chunk, _assets.BytesOf(shown), picked, lods, cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// A global chunk, loaded like a cube's (<see cref="LoadAsync"/>) but always as itself: it has no stand-in.
    /// </summary>
    private async Task<Loaded?> LoadGlobalAsync(Handle<Chunk> handle, CancellationToken cancel)
    {
        Chunk? chunk = await _assets.LoadAsync(handle, cancel: cancel).ConfigureAwait(false);
        if (chunk is null)
            return null;

        return await PrepareAsync(chunk, _assets.BytesOf(handle), 0, [], cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// The loaded chunk's entities read into component values, on a worker, and everything those name loaded.
    /// </summary>
    private async Task<Loaded> PrepareAsync(Chunk chunk, long bytes, int level, (float Threshold, Handle<Chunk> Asset)[] lods, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        PreparedChunk prepared = await _threads.InvokeAsync(ThreadId.Worker, cancel => _reader.Read(chunk, cancel), cancel).ConfigureAwait(false);

        // A chunk's entities mostly draw the same few things: each different component value is looked into once.
        HashSet<object> holders = [];
        foreach (ComponentColumn column in prepared.Columns)
            column.AddAssetHolders(holders);

        await _assets.LoadDependenciesAsync(holders, cancel: cancel).ConfigureAwait(false);

        // The scripts its entities carry, so they are made in the frame they spawn and not a few frames on.
        List<Task>? scripts = null;
        foreach (PreparedEntity entity in prepared.Entities)
        {
            foreach (JsonElement script in entity.Scripts)
            {
                if (script.ValueKind == JsonValueKind.Object && script.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.Number && id.TryGetUInt64(out ulong asset))
                    (scripts ??= []).Add(_assets.LoadAsync(new Handle<Script>(asset), cancel: cancel));
            }
        }

        if (scripts is not null)
            await Task.WhenAll(scripts).ConfigureAwait(false);

        return new Loaded(prepared, bytes, level, lods);
    }

    /// <summary>
    /// Finished loads wait as ready; the nearest, of any domain, spawn until this frame's <see cref="SpawnBudgetMs"/>
    /// is spent. One that is another level of detail of a spawned cube takes its place once the renderer has all of it
    /// (<see cref="Reveal"/>); without a renderer, there and then. A cube of a held domain waits for the renderer
    /// likewise before it counts as spawned, so the domain is all there when it is shown. A cancelled load is dropped, as is one whose chunk
    /// could not be read: the asset manager remembers that, so it is not begun again until its file changes.
    /// </summary>
    private void SpawnReady()
    {
        foreach (Stream stream in _streams)
        {
            SpawnGlobals(stream);
            CollectFinished(stream);
        }

        Reveal();

        // Entities are spawned in a group, whose cost only shows when it ends, so the budget is kept by count: as
        // many entities as fit in it at what the last frames' entities cost.
        long started = Stopwatch.GetTimestamp();
        int allowed = Math.Max(SpawnBatch, (int)((_bootstrapping ? BootstrapSpawnMs : SpawnBudgetMs) / _spawnMsPerEntity));
        int spawned = 0;
        while (spawned < allowed && (_spawning is not null || BeginSpawn()))
        {
            Spawning cube = _spawning;
            int from = cube.Next;
            bool whole = Spawn(cube.Loaded.Chunk, cube.Root, cube.Handles, ref cube.Next, allowed - spawned, cube.Stream.Held);
            spawned += cube.Next - from;
            if (!whole)
                break; // the rest of it next frame

            _spawning = null;
            if ((_ecs.Has<Hidden>(cube.Root) || (_drawn && cube.Stream.Held)) && !IsRegistered(cube))
                _revealing.Add(cube);
            else
                Finish(cube);
        }

        double elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (spawned >= SpawnBatch)
            _spawnMsPerEntity = Math.Max(1e-4, (_spawnMsPerEntity * 0.75) + (elapsedMs / spawned * 0.25));

        _fillWorstMs = Math.Max(_fillWorstMs, elapsedMs);
    }

    /// <summary>
    /// The cubes spawned whole that the renderer has caught up with take the place of the ones they replace.
    /// </summary>
    private void Reveal()
    {
        for (int i = _revealing.Count - 1; i >= 0; i--)
        {
            Spawning cube = _revealing[i];
            if (!IsRegistered(cube))
                continue;

            _revealing.RemoveAt(i);
            Finish(cube);
        }
    }

    /// <summary>
    /// Whether the renderer has an instance for every entity of the cube that draws. It goes on from where it stopped
    /// the frame before.
    /// </summary>
    private bool IsRegistered(Spawning cube)
    {
        for (; cube.Registered < cube.Handles.Length; cube.Registered++)
        {
            Handle entity = cube.Handles[cube.Registered];
            if (_ecs.IsAlive(entity) && _ecs.Has<Renderer>(entity) && _ecs.Has<WorldTransform>(entity) && !_ecs.Has<RenderInstance>(entity))
                return false;
        }

        return true;
    }

    /// <summary>
    /// The cube, all of it spawned, is the one spawned at its coordinate: what was there before is destroyed and, when
    /// it was hidden behind that, it is shown, both before this frame is drawn.
    /// </summary>
    private void Finish(Spawning cube)
    {
        (Stream stream, (int X, int Y, int Z) coordinate) = (cube.Stream, cube.Coordinate);
        if (stream.Spawned.Remove(coordinate, out (Handle<Chunk> Asset, Handle Root, int Entities, long Bytes, int Level, (float Threshold, Handle<Chunk> Asset)[] Lods) previous) && _ecs.IsAlive(previous.Root))
            _ecs.Destroy(previous.Root);

        _ecs.Remove<Hidden>(cube.Root);

        // Named once the one it replaces is gone: two children of one parent cannot share a name.
        _ecs.SetName(cube.Root, $"C{coordinate.X}_{coordinate.Y}_{coordinate.Z}");
        stream.Spawned[coordinate] = (cube.Asset, cube.Root, cube.Handles.Length, cube.Loaded.Bytes, cube.Loaded.Level, cube.Loaded.Lods);
        stream.Sizes[coordinate] = cube.Loaded.Bytes;
        stream.Fill.Remove(coordinate);
        Debugging.Log.Verbose($"Chunk {stream.Domain.Name} {coordinate.X}_{coordinate.Y}_{coordinate.Z} loaded{(cube.Loaded.Level > 0 ? $" as LOD {cube.Loaded.Level}" : "")}: {cube.Handles.Length} entities.");

        _fillChunks++;
    }

    /// <summary>
    /// Takes the nearest ready cube, of any domain, as the one being spawned; false when none is ready. One that is to
    /// replace a spawned cube is hidden until it does, when there is a renderer to say it is all there.
    /// </summary>
    [MemberNotNullWhen(true, nameof(_spawning))]
    private bool BeginSpawn()
    {
        if (NearestReady() is not { } next)
            return false;

        (Stream stream, (int X, int Y, int Z) coordinate) = next;
        stream.Ready.Remove(coordinate, out (Handle<Chunk> Asset, Loaded Loaded) ready);
        _spawning = new Spawning(stream, coordinate, ready.Asset, ready.Loaded, _ecs.Create(null, stream.ChunksRoot));
        if (_drawn && stream.Spawned.ContainsKey(coordinate))
            _ecs.Add<Hidden>(_spawning.Root);

        return true;
    }

    /// <summary>
    /// Whether the cube is being spawned, or is spawned and waiting to be shown.
    /// </summary>
    private bool IsSpawning(Stream stream, (int X, int Y, int Z) coordinate)
    {
        return (_spawning is { } cube && cube.Stream == stream && cube.Coordinate == coordinate) || IndexOfRevealing(stream, coordinate) >= 0;
    }

    private int IndexOfRevealing(Stream stream, (int X, int Y, int Z) coordinate)
    {
        for (int i = 0; i < _revealing.Count; i++)
        {
            if (_revealing[i].Stream == stream && _revealing[i].Coordinate == coordinate)
                return i;
        }

        return -1;
    }

    /// <summary>
    /// How many of the stream's cubes are waiting to be shown.
    /// </summary>
    private int Revealing(Stream stream)
    {
        int count = 0;
        foreach (Spawning cube in _revealing)
            count += cube.Stream == stream ? 1 : 0;

        return count;
    }

    /// <summary>
    /// Drops a cube waiting to be shown; what it was to replace stays.
    /// </summary>
    private void DropRevealing(int index)
    {
        Spawning cube = _revealing[index];
        _revealing.RemoveAt(index);
        if (_ecs.IsAlive(cube.Root))
            _ecs.Destroy(cube.Root);
    }

    /// <summary>
    /// Drops the cube being spawned with what there is of it; what it was to replace stays.
    /// </summary>
    private void DropSpawning()
    {
        if (_spawning is not { } cube)
            return;

        _spawning = null;
        if (_ecs.IsAlive(cube.Root))
            _ecs.Destroy(cube.Root);
    }

    /// <summary>
    /// Says, once nothing is loading any more, how long the chunks that streamed in since something was took.
    /// </summary>
    private void LogFill(int loading)
    {
        if (_fillStarted == 0)
        {
            if (loading == 0)
                return;

            _fillStarted = Stopwatch.GetTimestamp();
            _fillFrames = 0;
            _fillChunks = 0;
            _fillWorstMs = 0;
        }

        _fillFrames++;
        if (loading > 0)
            return;

        Debugging.Log.Verbose($"Streaming: {_fillChunks} chunk(s) in {Stopwatch.GetElapsedTime(_fillStarted).TotalMilliseconds:F0} ms over {_fillFrames} frame(s), {MaxLoads} loads at once; the most a frame spent spawning was {_fillWorstMs:F1} ms, at {_spawnMsPerEntity * 1000:F1} us an entity.");
        _fillStarted = 0;
    }

    /// <summary>
    /// Spawns the stream's global chunks that have arrived, in the order they were begun, so what a domain's globals
    /// look like does not depend on which file was read first. They are not held to the spawn budget: there are few,
    /// and everything else of the domain waits for them.
    /// </summary>
    private void SpawnGlobals(Stream stream)
    {
        while (stream.GlobalLoads.Count > 0 && stream.GlobalLoads[0].Task.IsCompleted)
        {
            (Handle<Chunk> asset, Task<Loaded?> task, CancellationTokenSource cancel) = stream.GlobalLoads[0];
            stream.GlobalLoads.RemoveAt(0);
            cancel.Dispose();

            if (task.IsFaulted)
                Debugging.Log.Error($"Global chunk {_assets.PathOf(asset.Id)} of {stream.Domain.Name} could not be prepared: {task.Exception.GetBaseException().Message}");
            else if (task.IsCompletedSuccessfully && task.Result is { } loaded)
                Spawn(loaded.Chunk, stream.GlobalsRoot, stream.Held);
        }
    }

    /// <summary>
    /// Moves the stream's finished loads to ready.
    /// </summary>
    private void CollectFinished(Stream stream)
    {
        _finished.Clear();
        foreach (((int X, int Y, int Z) coordinate, (_, Task<Loaded?> task, _)) in stream.Loading)
        {
            if (task.IsCompleted)
                _finished.Add(coordinate);
        }

        foreach ((int X, int Y, int Z) coordinate in _finished)
        {
            (Handle<Chunk> asset, Task<Loaded?> task, CancellationTokenSource cancel) = stream.Loading[coordinate];
            stream.Loading.Remove(coordinate);
            Loaded? loaded = task.IsCompletedSuccessfully ? task.Result : null;
            cancel.Dispose();

            if (task.IsFaulted)
            {
                // Spawned empty, so it is not begun again every frame; a change to its file reloads it.
                Debugging.Log.Error($"Chunk {stream.Domain.Name} {coordinate.X}_{coordinate.Y}_{coordinate.Z} could not be prepared: {task.Exception.GetBaseException().Message}");
                loaded = new Loaded(PreparedChunk.Empty, 0, 0, []);
            }

            if (loaded is not null)
                stream.Ready[coordinate] = (asset, loaded);
        }
    }

    private (Stream Stream, (int X, int Y, int Z) Chunk)? NearestReady()
    {
        (Stream Stream, (int X, int Y, int Z) Chunk)? nearest = null;
        float best = float.MaxValue;
        foreach (Stream stream in _streams)
        {
            foreach ((int X, int Y, int Z) coordinate in stream.Ready.Keys)
            {
                float distance = DistanceSquared(stream, coordinate);
                if (nearest is not null && distance >= best)
                    continue;

                nearest = (stream, coordinate);
                best = distance;
            }
        }

        return nearest;
    }

    /// <summary>
    /// How far a cube is from the nearest of its domain's cameras, squared; the cube a camera stands in comes before all.
    /// </summary>
    private float DistanceSquared(Stream stream, (int X, int Y, int Z) coordinate)
    {
        if (stream.Active.Contains(coordinate))
            return -1f;

        Vector3 centre = (new Vector3(coordinate.X, coordinate.Y, coordinate.Z) + new Vector3(0.5f)) * stream.Domain.ChunkSize;
        float nearest = float.MaxValue;
        foreach (Vector3 eye in stream.Cameras)
            nearest = MathF.Min(nearest, Vector3.DistanceSquared(centre, eye));

        return nearest;
    }

    /// <summary>
    /// Drops the loads nobody wants any more (no camera does, and they are not of the fill), then, while the spawned
    /// chunks of every domain are over the Chunk budget, unloads those out of view, the farthest first. Answers how
    /// many out of view are still spawned, what the spawned count for and the budget, both in bytes. A budget of 0
    /// keeps nothing that is out of view.
    /// </summary>
    private (int Kept, long Bytes, long Budget) UnloadOutOfView()
    {
        long bytes = 0;
        int count = 0;
        _candidates.Clear();
        foreach (Stream stream in _streams)
        {
            _unwanted.Clear();
            foreach ((int X, int Y, int Z) coordinate in stream.Loading.Keys)
            {
                if (!stream.Wanted.Contains(coordinate) && !stream.Fill.Contains(coordinate))
                    _unwanted.Add(coordinate);
            }

            foreach ((int X, int Y, int Z) coordinate in stream.Ready.Keys)
            {
                if (!stream.Wanted.Contains(coordinate) && !stream.Fill.Contains(coordinate))
                    _unwanted.Add(coordinate);
            }

            if (_spawning is { } cube && cube.Stream == stream && !stream.Wanted.Contains(cube.Coordinate) && !stream.Fill.Contains(cube.Coordinate))
                _unwanted.Add(cube.Coordinate);

            foreach ((int X, int Y, int Z) coordinate in _unwanted)
                Cancel(stream, coordinate);

            foreach (((int X, int Y, int Z) coordinate, (_, _, _, long size, _, _)) in stream.Spawned)
            {
                bytes += size;
                count++;
                if (!stream.Wanted.Contains(coordinate))
                    _candidates.Add((stream, coordinate));
            }
        }

        long budget = _assets.BudgetOf<Chunk>();
        if (_candidates.Count > 0 && (bytes > budget || budget == 0))
            _candidates.Sort(_nearestFirst);

        while (_candidates.Count > 0 && (bytes > budget || budget == 0))
        {
            (Stream stream, (int X, int Y, int Z) coordinate) = _candidates[^1];
            _candidates.RemoveAt(_candidates.Count - 1);
            bytes -= stream.Spawned[coordinate].Bytes;
            count--;
            Unload(stream, coordinate);
        }

        // For the fill, next frame: what is spawned, and how far the farthest cube kept is.
        _spawnedBytes = bytes;
        _spawnedCount = count;
        _farthestKept = null;
        foreach ((Stream stream, (int X, int Y, int Z) coordinate) in _candidates)
            _farthestKept = MathF.Max(_farthestKept ?? 0f, DistanceSquared(stream, coordinate));

        // What is still over the budget is all in view: nothing more can go.
        bool over = bytes > budget;
        if (over && !_overBudget)
            Debugging.Log.Warn($"Streaming: the chunks in view take {bytes / (1024d * 1024d):0.#} MB, over the {budget / (1024d * 1024d):0.#} MB Chunk budget (Assets.Budgets).");

        _overBudget = over;
        int kept = _candidates.Count;
        _candidates.Clear();

        return (kept, bytes, budget);
    }

    /// <summary>
    /// Drops the cube's load, in flight, ready, half spawned or waiting to be shown; what is spawned of it stays.
    /// </summary>
    private void Cancel(Stream stream, (int X, int Y, int Z) coordinate)
    {
        if (_spawning is { } cube && cube.Stream == stream && cube.Coordinate == coordinate)
            DropSpawning();

        int revealing = IndexOfRevealing(stream, coordinate);
        if (revealing >= 0)
            DropRevealing(revealing);

        stream.Fill.Remove(coordinate);
        stream.Ready.Remove(coordinate);
        if (stream.Loading.Remove(coordinate, out (Handle<Chunk> Asset, Task<Loaded?> Task, CancellationTokenSource Cancel) load))
            Abandon(load.Cancel);
    }

    private void Unload(Stream stream, (int X, int Y, int Z) coordinate)
    {
        Cancel(stream, coordinate);
        if (!stream.Spawned.Remove(coordinate, out (Handle<Chunk> Asset, Handle Root, int Entities, long Bytes, int Level, (float Threshold, Handle<Chunk> Asset)[] Lods) loaded))
            return;

        if (_ecs.IsAlive(loaded.Root))
            _ecs.Destroy(loaded.Root);

        Debugging.Log.Verbose($"Chunk {stream.Domain.Name} {coordinate.X}_{coordinate.Y}_{coordinate.Z} unloaded.");
    }

    /// <summary>
    /// Creates the chunk's entities under <paramref name="parent"/>, all of them now.
    /// </summary>
    private void Spawn(PreparedChunk chunk, Handle parent, bool held)
    {
        int next = 0;
        Spawn(chunk, parent, new Handle[chunk.Entities.Length], ref next, chunk.Entities.Length, held);
    }

    /// <summary>
    /// Creates up to <paramref name="count"/> of the chunk's entities under <paramref name="parent"/>, from
    /// <paramref name="next"/> on: the main thread's whole share of a chunk. True when they are all there; else
    /// <paramref name="next"/> is where the next frame goes on. <paramref name="handles"/> keeps the entities made
    /// so far, for their children. They are made as one group, so each lands where its components put it in a
    /// single move. The scripts of a <paramref name="held"/> domain's entities wait until it is loaded.
    /// </summary>
    private bool Spawn(PreparedChunk chunk, Handle parent, Handle[] handles, ref int next, int count, bool held)
    {
        using IDisposable group = _ecs.Group();
        PreparedEntity[] entities = chunk.Entities;
        for (int end = Math.Min(entities.Length, next + count); next < end; next++)
        {
            PreparedEntity entity = entities[next];
            Handle handle = handles[next] = _ecs.Create(entity.Name, entity.Parent < 0 ? parent : handles[entity.Parent]);
            if (entity.Id != 0)
                _ecs.Set(handle, new EntityId { Value = entity.Id });

            if (entity.Tags is { } tags)
            {
                // With the markers the tag system would otherwise put on next: all of it lands in one move.
                _ecs.Set(handle, tags);
                if (tags.Has(Tag.Static))
                    _ecs.Add<Static>(handle);
                if (tags.Has(Tag.Hidden))
                    _ecs.Add<Hidden>(handle);
            }

            for (int i = entity.FirstComponent, last = i + entity.ComponentCount; i < last; i++)
            {
                ComponentRef component = chunk.Components[i];
                ComponentColumn column = chunk.Columns[component.Column];
                column.Set(_ecs, handle, component.Row);

                // Its place in the world comes with it, in the same insert; the transform system would otherwise
                // move every new entity once more to give it one.
                if (column.Type == typeof(Transform))
                    _ecs.Add<WorldTransform>(handle);
            }

            _scripts?.Attach(handle, entity.Scripts, held);
        }

        return next >= entities.Length;
    }

    /// <summary>
    /// A cube as a worker hands it over: its entities, what it counts for, and the level of detail they are (0 the
    /// chunk itself, n the n-th of <see cref="Lods"/>, the chunk's lesser versions with the highest threshold first).
    /// </summary>
    private sealed record Loaded(PreparedChunk Chunk, long Bytes, int Level, (float Threshold, Handle<Chunk> Asset)[] Lods);

    /// <summary>
    /// A cube whose entities are being spawned, frame by frame, under a root that takes the cube's name once they are all there.
    /// </summary>
    private sealed class Spawning(Stream stream, (int X, int Y, int Z) coordinate, Handle<Chunk> asset, Loaded loaded, Handle root)
    {
        /// <summary>
        /// The index of the next entity to spawn.
        /// </summary>
        public int Next;

        /// <summary>
        /// The index of the first entity the renderer may not have registered yet.
        /// </summary>
        public int Registered;

        public Stream Stream { get; } = stream;

        public (int X, int Y, int Z) Coordinate { get; } = coordinate;

        public Handle<Chunk> Asset { get; } = asset;

        public Loaded Loaded { get; } = loaded;

        public Handle Root { get; } = root;

        public Handle[] Handles { get; } = new Handle[loaded.Chunk.Entities.Length];
    }

    /// <summary>
    /// One open domain: its asset as it was when opened, its entities' roots, its global chunks, and its cubes by
    /// coordinate: loading on a worker, then ready to spawn, then spawned under <see cref="ChunksRoot"/>. A spawned cube
    /// may be loading again, at another level of detail.
    /// </summary>
    private sealed class Stream(Handle<Domain> handle, Domain domain, Handle root, Handle globalsRoot, Handle chunksRoot)
    {
        public Handle<Domain> Handle { get; } = handle;

        public Domain Domain { get; } = domain;

        public Handle Root { get; } = root;

        public Handle GlobalsRoot { get; } = globalsRoot;

        public Handle ChunksRoot { get; } = chunksRoot;

        public List<Handle<Chunk>> Globals { get; } = [];

        /// <summary>
        /// The global chunks still loading, in the order they spawn.
        /// </summary>
        public List<(Handle<Chunk> Asset, Task<Loaded?> Task, CancellationTokenSource Cancel)> GlobalLoads { get; } = [];

        /// <summary>
        /// The domain is still loading in the world: not everything its cameras wanted has been spawned yet.
        /// </summary>
        public bool Filling { get; set; } = true;

        /// <summary>
        /// The domain was opened behind the loading domain and is not loaded yet: hidden, its scripts waiting.
        /// </summary>
        public bool Held { get; set; }

        /// <summary>
        /// The most of <see cref="Filling"/> the world was told, 0 to 1.
        /// </summary>
        public float Progress { get; set; }

        public Dictionary<(int X, int Y, int Z), (Handle<Chunk> Asset, Task<Loaded?> Task, CancellationTokenSource Cancel)> Loading { get; } = [];

        public Dictionary<(int X, int Y, int Z), (Handle<Chunk> Asset, Loaded Loaded)> Ready { get; } = [];

        /// <summary>
        /// Asset is the cube's own chunk file whatever Level is spawned.
        /// </summary>
        public Dictionary<(int X, int Y, int Z), (Handle<Chunk> Asset, Handle Root, int Entities, long Bytes, int Level, (float Threshold, Handle<Chunk> Asset)[] Lods)> Spawned { get; } = [];

        public HashSet<(int X, int Y, int Z)> Wanted { get; } = [];

        public HashSet<(int X, int Y, int Z)> Active { get; } = [];

        /// <summary>
        /// The cubes on their way that no camera wanted when they were begun: the fill, and the cubes out of view
        /// changing level. They are not dropped for being unwanted.
        /// </summary>
        public HashSet<(int X, int Y, int Z)> Fill { get; } = [];

        /// <summary>
        /// What each cube counted for the last time it was spawned, in bytes.
        /// </summary>
        public Dictionary<(int X, int Y, int Z), long> Sizes { get; } = [];

        /// <summary>
        /// Where the cameras that want of this domain are, as of this frame.
        /// </summary>
        public List<Vector3> Cameras { get; } = [];

        /// <summary>
        /// The cubes that have a chunk file; null = look again.
        /// </summary>
        public Dictionary<(int X, int Y, int Z), Handle<Chunk>>? Files { get; set; }
    }
}
