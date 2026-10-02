using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.Events;
using Magic.Interfaces;
using Magic.Mathematics;
using Magic.Services;
using Magic.Utils;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Text.Json;

namespace Magic.Systems.Streaming;

/// <summary>What the streaming system is doing, over every open domain, for the debug display and the logs.</summary>
internal readonly record struct StreamingStats(int Domains, int Cameras, int Wanted, int Active, int Loaded, int Loading, int PendingUnload, int Entities);

/// <summary>
/// Keeps the ECS filled with the open <see cref="Domain"/>s. It is told what is open by events alone:
/// <see cref="EventType.DomainOpened"/> spawns a domain's global chunks under <c>World.&lt;Name&gt;.Globals</c>, where
/// they stay, and <see cref="EventType.DomainClosed"/> destroys everything of it. In between, every frame, for every
/// camera, the chunk cube the camera stands in (active), the cubes within one chunk size of it, and the cubes its
/// frustum touches within <see cref="RenderSettings.ViewDist"/> are wanted: their <c>x_y_z.chunk</c> files, when they
/// exist beside the domain file, load on a worker, nearest first and <see cref="MaxLoads"/> at a time, where their
/// components are read too; the main thread only spawns them, nearest first and <see cref="SpawnBudgetMs"/> worth a
/// frame, under <c>World.&lt;Name&gt;.Chunks.Cx_y_z</c>. A cube nobody wants for <see cref="UnloadDelayFrames"/>
/// frames is destroyed again. Both budgets are shared by all open domains. This is the only code that reads a chunk's
/// entities: a component is set from its JSON by the name it was written under, resolved against every loaded
/// assembly, so gems add components without registering anything; an entity's scripts go to the
/// <see cref="ScriptSystem"/> as they are. Asset and gem changes arrive in
/// <see cref="Frame.Events"/> too. It runs in Update.
/// </summary>
internal sealed class StreamingSystem : ISystem
{
    /// <summary>Frames a chunk stays after the last camera stopped wanting it, so a turn does not thrash.</summary>
    private const int UnloadDelayFrames = 120;

    /// <summary>Chunk loads in flight at once; the rest wait their turn, nearest first, so the near ones are not queued behind the far.</summary>
    private const int MaxLoads = 4;

    /// <summary>Milliseconds a frame may spend spawning; at least one chunk always goes, so a big one cannot stall streaming.</summary>
    private const double SpawnBudgetMs = 4;

    /// <summary>The frustum is built wider than any window so an edge cube is never missed.</summary>
    private const float StreamingAspect = 2f;

    private readonly IEcs _ecs;
    private readonly Assets _assets;
    private readonly Project _project;
    private readonly ScriptSystem? _scripts;
    private readonly IEcsQuery<Camera, WorldTransform> _cameras;

    // One per open domain, in the order they were opened; their roots hang under the one World entity.
    private readonly List<Stream> _streams = [];
    private Handle _root;
    private readonly List<Vector3> _cameraPositions = [];
    private readonly List<(int X, int Y, int Z)> _finished = [];
    private readonly List<(int X, int Y, int Z)> _unwanted = [];
    private readonly List<(Stream Stream, (int X, int Y, int Z) Chunk)> _candidates = [];
    private readonly Comparison<(Stream Stream, (int X, int Y, int Z) Chunk)> _nearestFirst; // one delegate, not one per frame
    private long _frameNumber;
    private int _cameraCount;

    // Component names -> types, read by workers; replaced whole when gems change.
    private volatile ComponentTypes? _types;
    private readonly HashSet<string> _warned = []; // locked: workers warn too

    public StreamingSystem(IEcs ecs, Assets assets, Project project, ScriptSystem? scripts)
    {
        _ecs = ecs;
        _assets = assets;
        _project = project;
        _scripts = scripts;
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

    /// <summary>One frame of streaming: this frame's domain, asset and gem events, finished loads, then what the cameras want.</summary>
    public void Run(in Frame frame)
    {
        _frameNumber++;
        foreach (Event change in frame.Events.Span)
            Apply(change);

        SpawnReady();

        _cameraPositions.Clear();
        _cameraCount = 0;
        if (_streams.Count == 0)
        {
            Stats = default;
            return;
        }

        foreach (Stream stream in _streams)
        {
            FindWanted(stream, first: stream == _streams[0]);
            foreach ((int X, int Y, int Z) coordinate in stream.Wanted)
                stream.UnneededSince.Remove(coordinate);
        }

        LoadNearest();

        int pending = 0, wanted = 0, active = 0, loaded = 0, loading = 0, entities = 0;
        foreach (Stream stream in _streams)
        {
            pending += UnloadUnwanted(stream);
            wanted += stream.Wanted.Count;
            active += stream.Active.Count;
            loaded += stream.Spawned.Count;
            loading += stream.Loading.Count + stream.Ready.Count;
            foreach ((_, _, int count) in stream.Spawned.Values)
                entities += count;
        }

        Stats = new StreamingStats(_streams.Count, _cameraCount, wanted, active, loaded, loading, pending, entities);
    }

    /// <summary>
    /// The cubes one camera needs: the one it stands in (active), every cube within one chunk size of it wherever it
    /// looks (so turning never waits on a load), and every cube within <paramref name="viewDist"/> metres that its
    /// frustum touches. <paramref name="frustum"/> is camera-relative, as <see cref="Camera.Frustum"/>
    /// gives; <paramref name="viewDist"/> is finite (the caller cuts it to the domain's extent).
    /// </summary>
    private static void Select(float chunkSize, Vector3 camera, in Frustum frustum, float viewDist, HashSet<(int X, int Y, int Z)> wanted, HashSet<(int X, int Y, int Z)>? active = null)
    {
        (int X, int Y, int Z) at = Coordinate(camera, chunkSize);
        wanted.Add(at);
        active?.Add(at);

        int reach = (int)MathF.Ceiling(MathF.Max(viewDist, chunkSize) / chunkSize);
        float near2 = chunkSize * chunkSize;
        float far2 = viewDist * viewDist;
        for (int z = at.Z - reach; z <= at.Z + reach; z++)
        {
            for (int y = at.Y - reach; y <= at.Y + reach; y++)
            {
                for (int x = at.X - reach; x <= at.X + reach; x++)
                {
                    Vector3 min = (new Vector3(x, y, z) * chunkSize) - camera;
                    Aabb box = new(min, min + new Vector3(chunkSize));
                    float distance2 = box.DistanceSquared(Vector3.Zero);
                    if (distance2 <= near2 || (distance2 <= far2 && frustum.Intersects(box)))
                        wanted.Add((x, y, z));
                }
            }
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
    /// A domain opens or closes as the world said; a changed domain file reopens it, a changed global chunk respawns
    /// its globals, a changed cube reloads, a removed file goes; new gems may bring component types.
    /// </summary>
    private void Apply(Event change)
    {
        if (change.Type is EventType.AssetAdded or EventType.AssetRemoved)
        {
            foreach (Stream stream in _streams)
                stream.Extent = null;
        }

        switch (change.Type)
        {
            case EventType.DomainOpened:
                Open(new Handle<Domain>(change.Id), _streams.Count);
                break;
            case EventType.DomainClosed when Find(change.Id) is { } closed:
                Close(closed);
                break;
            case EventType.GemsChanged:
                _types = null;
                lock (_warned)
                    _warned.Clear();
                foreach (Stream stream in _streams)
                    DropUnspawned(stream);
                break;
            case EventType.AssetRemoved when Find(change.Id) is { } removed:
                Close(removed);
                break;
            case EventType.AssetModified when Find(change.Id) is { } modified:
                int index = _streams.IndexOf(modified);
                Close(modified);
                Open(modified.Handle, index); // in its old place, so the order holds
                break;
            case EventType.AssetModified when _streams.Find(open => open.Globals.Exists(chunk => chunk.Id == change.Id)) is { } owner:
                ReloadGlobals(owner);
                break;
            case EventType.AssetRemoved:
            case EventType.AssetModified:
                ForgetChunk(change.Id); // a modified one is wanted again next frame, so it reloads with the new content
                break;
        }
    }

    /// <summary>
    /// Spawns the domain's globals under a root of its own; its cubes stream in from this frame on. One that is open
    /// already, cannot be loaded, or is named like an open one is left alone (the last two logged).
    /// </summary>
    private void Open(Handle<Domain> handle, int index)
    {
        if (Find(handle.Id) is not null)
            return;

        Domain? domain = _assets.Load(handle);
        if (domain is null)
            return; // the asset manager logged why

        if (_streams.Exists(open => open.Domain.Name == domain.Name))
        {
            Debugging.Log.Error($"Domain {domain.Path} cannot be opened: another open domain is named {domain.Name}.");
            return;
        }

        if (!_root.IsValid || !_ecs.IsAlive(_root))
            _root = _ecs.Create("World");

        Handle root = _ecs.Create(domain.Name, _root);
        Stream stream = new(handle, domain, root, _ecs.Create("Globals", root), _ecs.Create("Chunks", root));
        _streams.Insert(index, stream);

        LoadGlobals(stream);
        Debugging.Log.Info($"Domain {domain.Name} opened: {stream.Globals.Count} global chunk(s), chunk size {domain.ChunkSize} m.");
    }

    private void Close(Stream stream)
    {
        DropUnspawned(stream);
        _streams.Remove(stream);
        if (_ecs.IsAlive(stream.Root))
            _ecs.Destroy(stream.Root);

        Debugging.Log.Info($"Domain {stream.Domain.Name} closed.");
    }

    private void LoadGlobals(Stream stream)
    {
        foreach (Handle<Chunk> handle in _assets.FindAll<Chunk>(stream.Domain.Folder, ".chunk"))
        {
            if (Chunk.ParseCoordinate(Path.GetFileNameWithoutExtension(_assets.PathOf(handle.Id) ?? "")) is not null)
                continue;

            stream.Globals.Add(handle);
            if (_assets.Load(handle) is { } chunk)
                Spawn(Prepare(chunk), stream.GlobalsRoot);
        }
    }

    private void ReloadGlobals(Stream stream)
    {
        foreach (Handle child in _ecs.GetChildren(stream.GlobalsRoot))
            _ecs.Destroy(child);

        stream.Globals.Clear();
        LoadGlobals(stream);
    }

    /// <summary>
    /// Drops every chunk of the stream not spawned yet. When gems change, its components may be of a gem that just
    /// went: the cubes are still wanted, so they load again next frame with the types there are now.
    /// </summary>
    private static void DropUnspawned(Stream stream)
    {
        foreach ((_, _, CancellationTokenSource cancel) in stream.Loading.Values)
            cancel.Cancel();

        stream.Loading.Clear();
        stream.Ready.Clear();
    }

    /// <summary>Drops a streamed chunk by asset id, loaded, ready or loading; a no-op for other ids.</summary>
    private void ForgetChunk(ulong id)
    {
        foreach (Stream stream in _streams)
        {
            foreach (((int X, int Y, int Z) coordinate, (Handle<Chunk> asset, _, _)) in stream.Spawned)
            {
                if (asset.Id != id)
                    continue;

                Unload(stream, coordinate);
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

    /// <summary>What the cameras want of one domain. The <paramref name="first"/> stream of a frame also notes where the cameras are.</summary>
    private void FindWanted(Stream stream, bool first)
    {
        stream.Wanted.Clear();
        stream.Active.Clear();

        float chunkSize = stream.Domain.ChunkSize;
        float viewDist = MathF.Max(0f, _project.Settings.Render.ViewDist);
        ((int X, int Y, int Z) Min, (int X, int Y, int Z) Max) extent = stream.Extent ??= Extent(stream);
        Vector3 min = new Vector3(extent.Min.X, extent.Min.Y, extent.Min.Z) * chunkSize;
        Vector3 max = (new Vector3(extent.Max.X, extent.Max.Y, extent.Max.Z) + Vector3.One) * chunkSize;

        _cameras.Run((ReadOnlySpan<Handle> _, Span<Camera> cameras, Span<WorldTransform> worlds) =>
        {
            for (int i = 0; i < cameras.Length; i++)
            {
                Frustum frustum = cameras[i].Frustum(worlds[i].Value, StreamingAspect);
                Vector3 eye = worlds[i].Value.Translation;

                // No farther than the domain's farthest chunk: an infinite (or huge) view distance walks only cubes there are.
                float farthest = Vector3.Max(Vector3.Abs(min - eye), Vector3.Abs(max - eye)).Length();
                Select(chunkSize, eye, frustum, MathF.Min(viewDist, farthest), stream.Wanted, stream.Active);
                if (!first)
                    continue;

                _cameraPositions.Add(eye);
                _cameraCount++;
            }
        });
    }

    /// <summary>The lowest and highest coordinates among the domain's chunk files; the origin's cube when it has none.</summary>
    private ((int X, int Y, int Z) Min, (int X, int Y, int Z) Max) Extent(Stream stream)
    {
        (int X, int Y, int Z) min = (int.MaxValue, int.MaxValue, int.MaxValue), max = (int.MinValue, int.MinValue, int.MinValue);
        foreach (Handle<Chunk> handle in _assets.FindAll<Chunk>(stream.Domain.Folder, ".chunk"))
        {
            if (Chunk.ParseCoordinate(Path.GetFileNameWithoutExtension(_assets.PathOf(handle.Id) ?? "")) is not { } coordinate)
                continue;

            min = (Math.Min(min.X, coordinate.X), Math.Min(min.Y, coordinate.Y), Math.Min(min.Z, coordinate.Z));
            max = (Math.Max(max.X, coordinate.X), Math.Max(max.Y, coordinate.Y), Math.Max(max.Z, coordinate.Z));
        }

        return min.X > max.X ? ((0, 0, 0), (0, 0, 0)) : (min, max);
    }

    /// <summary>Starts the nearest wanted cubes, of any domain, that are not in hand yet, while fewer than <see cref="MaxLoads"/> are loading.</summary>
    private void LoadNearest()
    {
        int loading = 0;
        foreach (Stream stream in _streams)
            loading += stream.Loading.Count;

        if (loading >= MaxLoads)
            return;

        _candidates.Clear();
        foreach (Stream stream in _streams)
        {
            foreach ((int X, int Y, int Z) coordinate in stream.Wanted)
            {
                if (!stream.Spawned.ContainsKey(coordinate) && !stream.Loading.ContainsKey(coordinate) && !stream.Ready.ContainsKey(coordinate))
                    _candidates.Add((stream, coordinate));
            }
        }

        _candidates.Sort(_nearestFirst);
        for (int i = 0; i < _candidates.Count && loading < MaxLoads; i++)
        {
            if (StartLoad(_candidates[i].Stream, _candidates[i].Chunk))
                loading++;
        }

        _candidates.Clear();
    }

    /// <summary>Whether a load began: not for a cube with no file, or one that failed before.</summary>
    private bool StartLoad(Stream stream, (int X, int Y, int Z) coordinate)
    {
        Handle<Chunk> handle = _assets.Find<Chunk>($"{stream.Domain.Folder}/{Chunk.FileName(coordinate.X, coordinate.Y, coordinate.Z)}");
        if (!handle.IsValid || _assets.HasFailed(handle))
            return false;

        CancellationTokenSource cancel = new();
        stream.Loading[coordinate] = (handle, Load(handle, cancel.Token), cancel);

        return true;
    }

    /// <summary>
    /// The chunk, read and prepared on a worker, done only once every asset its components have a handle to has been
    /// loaded there with what it names in turn. They land in the asset manager's pools, so the renderer's own loads in
    /// the frame the chunk spawns find them there instead of reading and importing on the main thread; one already
    /// pooled costs nothing.
    /// </summary>
    private Task<Prepared[]?> Load(Handle<Chunk> handle, CancellationToken cancel)
    {
        return Task.Run(async () =>
        {
            if (_assets.Load(handle) is not { } chunk)
                return null;

            Prepared[] entities = Prepare(chunk);

            await _assets.LoadDependenciesAsync(entities.SelectMany(entity => entity.Components.Select(component => component.Value)), cancel);

            return entities;
        }, cancel);
    }

    /// <summary>
    /// Finished loads wait as ready; the nearest, of any domain, spawn until this frame's <see cref="SpawnBudgetMs"/>
    /// is spent. A cancelled load is dropped, as is one whose chunk could not be read: the asset manager remembers
    /// that, so it is not begun again until its file changes.
    /// </summary>
    private void SpawnReady()
    {
        foreach (Stream stream in _streams)
            CollectFinished(stream);

        long started = Stopwatch.GetTimestamp();
        while (NearestReady() is { } next)
        {
            (Stream stream, (int X, int Y, int Z) coordinate) = next;
            stream.Ready.Remove(coordinate, out (Handle<Chunk> Asset, Prepared[] Entities) ready);

            Handle root = _ecs.Create($"C{coordinate.X}_{coordinate.Y}_{coordinate.Z}", stream.ChunksRoot);
            int entities = Spawn(ready.Entities, root);
            stream.Spawned[coordinate] = (ready.Asset, root, entities);
            Debugging.Log.Verbose($"Chunk {stream.Domain.Name} {coordinate.X}_{coordinate.Y}_{coordinate.Z} loaded: {entities} entities.");

            if (Stopwatch.GetElapsedTime(started).TotalMilliseconds >= SpawnBudgetMs)
                break;
        }
    }

    /// <summary>Moves the stream's finished loads to ready.</summary>
    private void CollectFinished(Stream stream)
    {
        _finished.Clear();
        foreach (((int X, int Y, int Z) coordinate, (_, Task<Prepared[]?> task, _)) in stream.Loading)
        {
            if (task.IsCompleted)
                _finished.Add(coordinate);
        }

        foreach ((int X, int Y, int Z) coordinate in _finished)
        {
            (Handle<Chunk> asset, Task<Prepared[]?> task, CancellationTokenSource cancel) = stream.Loading[coordinate];
            stream.Loading.Remove(coordinate);
            Prepared[]? entities = task.IsCompletedSuccessfully ? task.Result : null;
            cancel.Dispose();

            if (task.IsFaulted)
            {
                // Spawned empty, so it is not begun again every frame; a change to its file reloads it.
                Debugging.Log.Error($"Chunk {stream.Domain.Name} {coordinate.X}_{coordinate.Y}_{coordinate.Z} could not be prepared: {task.Exception.GetBaseException().Message}");
                entities = [];
            }

            if (entities is not null)
                stream.Ready[coordinate] = (asset, entities);
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

    /// <summary>How far a cube is from the nearest camera, squared; the cube a camera stands in comes before all.</summary>
    private float DistanceSquared(Stream stream, (int X, int Y, int Z) coordinate)
    {
        if (stream.Active.Contains(coordinate))
            return -1f;

        Vector3 centre = (new Vector3(coordinate.X, coordinate.Y, coordinate.Z) + new Vector3(0.5f)) * stream.Domain.ChunkSize;
        float nearest = float.MaxValue;
        foreach (Vector3 eye in _cameraPositions)
            nearest = MathF.Min(nearest, Vector3.DistanceSquared(centre, eye));

        return nearest;
    }

    /// <summary>Unloads what nobody wanted for <see cref="UnloadDelayFrames"/> frames; answers how many are still waiting that out.</summary>
    private int UnloadUnwanted(Stream stream)
    {
        _unwanted.Clear();
        foreach ((int X, int Y, int Z) coordinate in stream.Spawned.Keys)
        {
            if (!stream.Wanted.Contains(coordinate))
                _unwanted.Add(coordinate);
        }

        foreach ((int X, int Y, int Z) coordinate in stream.Loading.Keys)
        {
            if (!stream.Wanted.Contains(coordinate))
                _unwanted.Add(coordinate);
        }

        foreach ((int X, int Y, int Z) coordinate in stream.Ready.Keys)
        {
            if (!stream.Wanted.Contains(coordinate))
                _unwanted.Add(coordinate);
        }

        int pending = 0;
        foreach ((int X, int Y, int Z) coordinate in _unwanted)
        {
            if (!stream.UnneededSince.TryGetValue(coordinate, out long unneededSince))
                stream.UnneededSince[coordinate] = unneededSince = _frameNumber;

            if (_frameNumber - unneededSince >= UnloadDelayFrames)
                Unload(stream, coordinate);
            else
                pending++;
        }

        return pending;
    }

    private void Unload(Stream stream, (int X, int Y, int Z) coordinate)
    {
        stream.UnneededSince.Remove(coordinate);
        stream.Ready.Remove(coordinate);
        if (stream.Loading.Remove(coordinate, out (Handle<Chunk> Asset, Task<Prepared[]?> Task, CancellationTokenSource Cancel) load))
            load.Cancel.Cancel();

        if (!stream.Spawned.Remove(coordinate, out (Handle<Chunk> Asset, Handle Root, int Entities) loaded))
            return;

        if (_ecs.IsAlive(loaded.Root))
            _ecs.Destroy(loaded.Root);

        Debugging.Log.Verbose($"Chunk {stream.Domain.Name} {coordinate.X}_{coordinate.Y}_{coordinate.Z} unloaded.");
    }

    /// <summary>Creates the prepared entities under <paramref name="parent"/>; the main thread's whole share of a chunk.</summary>
    private int Spawn(Prepared[] entities, Handle parent)
    {
        Handle[] handles = new Handle[entities.Length];
        for (int i = 0; i < entities.Length; i++)
        {
            Prepared entity = entities[i];
            Handle handle = handles[i] = _ecs.Create(entity.Name, entity.Parent < 0 ? parent : handles[entity.Parent]);
            if (entity.Id != 0)
                _ecs.Set(handle, new EntityId { Value = entity.Id });

            if (entity.Tags is { } tags)
                _ecs.Set(handle, tags);

            foreach ((Action<IEcs, Handle, object> set, object value) in entity.Components)
                set(_ecs, handle, value);

            _scripts?.Attach(handle, entity.Scripts);
        }

        return handles.Length;
    }

    /// <summary>
    /// The chunk's entities flattened parent-first, their component names resolved and JSON read into values: all of a
    /// spawn that does not touch the ECS, so it can run on a worker. Components that cannot be read are warned about
    /// once and skipped.
    /// </summary>
    private Prepared[] Prepare(Chunk chunk)
    {
        ComponentTypes types = _types ??= new ComponentTypes(FindComponentTypes());
        List<Prepared> entities = [];
        foreach (Chunk.Entity entity in chunk.Entities)
            Prepare(entity, -1, chunk.Path, types, entities);

        return [.. entities];
    }

    private void Prepare(Chunk.Entity entity, int parent, string file, ComponentTypes types, List<Prepared> entities)
    {
        Tags? tags = null;
        if (entity.Tags.Length > 0)
        {
            if (entity.Tags.Length > Tags.Capacity)
                Debugging.Log.Warn($"{file}: entity {entity.Name ?? "(unnamed)"} has {entity.Tags.Length} tags; only {Tags.Capacity} fit.");

            Span<Tag> span = stackalloc Tag[Math.Min(entity.Tags.Length, Tags.Capacity)];
            for (int i = 0; i < span.Length; i++)
                span[i] = Tag.Of(entity.Tags[i]);
            tags = Tags.Of(span);
        }

        List<(Action<IEcs, Handle, object>, object)> components = new(entity.Components.Count);
        foreach ((string name, JsonElement json) in entity.Components)
        {
            Type? type = Resolve(types, name, file);
            if (type is null)
                continue;

            try
            {
                object value = JsonSerializer.Deserialize(json, type, AssetJson.Options) ?? throw new JsonException("null");
                components.Add((types.Setter(type), value));
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
            {
                Warn($"{file}:{name}", $"{file}: component {name} on {entity.Name ?? "(unnamed)"} could not be read as {type.Name}: {ex.Message}");
            }
        }

        int index = entities.Count;
        entities.Add(new Prepared(parent, entity.Name, entity.Id, tags, [.. components], entity.Scripts));
        foreach (Chunk.Entity child in entity.Children)
            Prepare(child, index, file, types, entities);
    }

    /// <summary>The struct a component name means: its type name or full name, among every <see cref="IComponent"/> in the process.</summary>
    private Type? Resolve(ComponentTypes types, string name, string file)
    {
        if (!types.ByName.TryGetValue(name, out List<Type>? found))
        {
            Warn(name, $"{file}: no loaded component type is named {name}; skipped.");
            return null;
        }

        if (found.Count == 1)
            return found[0];

        Warn(name, $"{file}: {name} names {found.Count} component types ({string.Join(", ", found.Select(type => type.FullName))}); use the full name.");
        return null;
    }

    /// <summary>Logs <paramref name="message"/> the first time <paramref name="key"/> is seen since gems last changed.</summary>
    private void Warn(string key, string message)
    {
        lock (_warned)
        {
            if (!_warned.Add(key))
                return;
        }

        Debugging.Log.Warn(message);
    }

    private static Dictionary<string, List<Type>> FindComponentTypes()
    {
        // Only Core and what references it can declare an IComponent; skipping the framework is most of the time saved.
        Assembly core = typeof(IComponent).Assembly;
        string? coreName = core.GetName().Name;
        Dictionary<string, List<Type>> found = new(StringComparer.OrdinalIgnoreCase);
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic || (assembly != core && !assembly.GetReferencedAssemblies().Any(reference => reference.Name == coreName)))
                continue;

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(type => type is not null).ToArray()!;
            }

            foreach (Type type in types)
            {
                if (!type.IsValueType || type.IsGenericTypeDefinition || !typeof(IComponent).IsAssignableFrom(type))
                    continue;

                Add(found, type.Name, type);
                if (type.FullName is { } full && full != type.Name)
                    Add(found, full, type);
            }
        }

        return found;

        static void Add(Dictionary<string, List<Type>> found, string key, Type type)
        {
            if (!found.TryGetValue(key, out List<Type>? list))
                found[key] = list = [];

            if (!list.Contains(type))
                list.Add(type);
        }
    }

    private static void SetBoxed<T>(IEcs ecs, Handle entity, object value) where T : unmanaged
    {
        ecs.Set(entity, (T)value);
    }

    /// <summary>One entity of a chunk as the main thread spawns it; <see cref="Parent"/> indexes an earlier entity, or is -1 for the chunk's root.</summary>
    private readonly record struct Prepared(int Parent, string? Name, ulong Id, Tags? Tags, (Action<IEcs, Handle, object> Set, object Value)[] Components, JsonElement[] Scripts);

    /// <summary>
    /// One open domain: its asset as it was when opened, its entities' roots, its global chunks, and its cubes by
    /// coordinate: loading on a worker, then ready to spawn, then spawned under <see cref="ChunksRoot"/>.
    /// </summary>
    private sealed class Stream(Handle<Domain> handle, Domain domain, Handle root, Handle globalsRoot, Handle chunksRoot)
    {
        public Handle<Domain> Handle { get; } = handle;

        public Domain Domain { get; } = domain;

        public Handle Root { get; } = root;

        public Handle GlobalsRoot { get; } = globalsRoot;

        public Handle ChunksRoot { get; } = chunksRoot;

        public List<Handle<Chunk>> Globals { get; } = [];

        public Dictionary<(int X, int Y, int Z), (Handle<Chunk> Asset, Task<Prepared[]?> Task, CancellationTokenSource Cancel)> Loading { get; } = [];

        public Dictionary<(int X, int Y, int Z), (Handle<Chunk> Asset, Prepared[] Entities)> Ready { get; } = [];

        public Dictionary<(int X, int Y, int Z), (Handle<Chunk> Asset, Handle Root, int Entities)> Spawned { get; } = [];

        public Dictionary<(int X, int Y, int Z), long> UnneededSince { get; } = [];

        public HashSet<(int X, int Y, int Z)> Wanted { get; } = [];

        public HashSet<(int X, int Y, int Z)> Active { get; } = [];

        /// <summary>Of the domain's chunk files; null = look again.</summary>
        public ((int X, int Y, int Z) Min, (int X, int Y, int Z) Max)? Extent { get; set; }
    }

    /// <summary>Every <see cref="IComponent"/> by name, and the generic Set&lt;T&gt; for each, as one snapshot workers share.</summary>
    private sealed class ComponentTypes(Dictionary<string, List<Type>> byName)
    {
        private readonly ConcurrentDictionary<Type, Action<IEcs, Handle, object>> _setters = new();

        public Dictionary<string, List<Type>> ByName { get; } = byName;

        /// <summary>Throws ArgumentException for a struct that is not unmanaged.</summary>
        public Action<IEcs, Handle, object> Setter(Type type)
        {
            return _setters.GetOrAdd(type, static component => typeof(StreamingSystem)
                .GetMethod(nameof(SetBoxed), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(component)
                .CreateDelegate<Action<IEcs, Handle, object>>());
        }
    }
}
