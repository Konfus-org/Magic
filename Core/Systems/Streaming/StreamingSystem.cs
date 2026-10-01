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

/// <summary>What the streaming system is doing, for logs and tests.</summary>
public readonly record struct StreamingStats(int Cameras, int Wanted, int Active, int Loaded, int Loading, int PendingUnload, int Entities);

/// <summary>
/// Keeps the ECS filled with the part of the current <see cref="World"/> the cameras can see. A world is
/// selected with <see cref="Open"/>; its global chunks are spawned under <c>World.Globals</c> and stay. Every frame, for every camera, the chunk cube the camera
/// stands in (active), the cubes within one chunk size of it, and the cubes its frustum touches within
/// <see cref="RenderSettings.ViewDist"/> are wanted: their <c>x_y_z.chunk</c> files, when they exist beside the world file, load on a worker, nearest first and
/// <see cref="MaxLoads"/> at a time, where their components are read too; the main thread only spawns them, nearest
/// first and <see cref="SpawnBudgetMs"/> worth a frame, under <c>World.Chunks.Cx_y_z</c>. A cube nobody wants for
/// <see cref="UnloadDelayFrames"/> frames is destroyed again. This is the only code that reads a chunk's entities: a
/// component is set from its JSON by the name it was written under, resolved against every loaded assembly, so gems
/// add components without registering anything. Asset and gem changes arrive in <see cref="Frame.Events"/>. The frame
/// loop calls it after every gem's Update.
/// </summary>
public sealed class StreamingSystem : IDisposable
{
    /// <summary>Frames a chunk stays after the last camera stopped wanting it, so a turn does not thrash.</summary>
    public const int UnloadDelayFrames = 120;

    /// <summary>Chunk loads in flight at once; the rest wait their turn, nearest first, so the near ones are not queued behind the far.</summary>
    public const int MaxLoads = 4;

    /// <summary>Milliseconds a frame may spend spawning; at least one chunk always goes, so a big one cannot stall streaming.</summary>
    public const double SpawnBudgetMs = 4;

    /// <summary>The frustum is built wider than any window so an edge cube is never missed.</summary>
    private const float StreamingAspect = 2f;

    private readonly IEcs _ecs;
    private readonly Assets _assets;
    private readonly Project _project;
    private readonly IEcsQuery<Camera, WorldTransform> _cameras;

    // The selected world.
    private World? _world;
    private string _folder = "";
    private Handle _globalsRoot, _chunksRoot;
    private readonly List<Handle<Chunk>> _globals = [];

    // Streamed chunks by coordinate: loading on a worker, then ready to spawn, then spawned.
    private readonly Dictionary<(int X, int Y, int Z), (Handle<Chunk> Asset, Task<Prepared[]?> Task, CancellationTokenSource Cancel)> _loading = [];
    private readonly Dictionary<(int X, int Y, int Z), (Handle<Chunk> Asset, Prepared[] Entities)> _ready = [];
    private readonly Dictionary<(int X, int Y, int Z), (Handle<Chunk> Asset, Handle Root, int Entities)> _chunks = [];
    private readonly Dictionary<(int X, int Y, int Z), long> _unneededSince = [];
    private readonly HashSet<ulong> _failed = [];
    private readonly HashSet<(int X, int Y, int Z)> _wanted = [];
    private readonly HashSet<(int X, int Y, int Z)> _active = [];
    private readonly List<Vector3> _eyes = [];
    private readonly List<(int X, int Y, int Z)> _scratch = [];
    private readonly Comparison<(int X, int Y, int Z)> _nearer; // one delegate, not one per frame
    private long _frame;
    private int _cameraCount;
    private ((int X, int Y, int Z) Min, (int X, int Y, int Z) Max)? _extent; // of the world's chunk files; null = look again

    // Component names -> types, read by workers; replaced whole when gems change.
    private volatile ComponentTypes? _types;
    private readonly HashSet<string> _warned = []; // locked: workers warn too

    public StreamingSystem(IEcs ecs, Assets assets, Project project)
    {
        _ecs = ecs;
        _assets = assets;
        _project = project;
        _cameras = ecs.Query<Camera, WorldTransform>().Build();
        _nearer = (a, b) => Distance(a).CompareTo(Distance(b));
    }

    /// <summary>The selected world; None until one was opened.</summary>
    public Handle<World> World { get; private set; }

    /// <summary>The entity everything of the world hangs under (<c>World</c>); None when no world is selected.</summary>
    public Handle Root { get; private set; }

    public IReadOnlyCollection<(int X, int Y, int Z)> Loaded => _chunks.Keys;

    public StreamingStats Stats { get; private set; }

    public bool IsActive((int X, int Y, int Z) chunk)
    {
        return _active.Contains(chunk);
    }

    /// <summary>
    /// Makes <paramref name="handle"/> the world: destroys the old one's entities and spawns the new one's globals;
    /// its chunks stream in from the next <see cref="Update"/>. A world that cannot be loaded leaves none (logged).
    /// </summary>
    public void Open(Handle<World> handle)
    {
        Close();

        World? world = _assets.Load(handle);
        if (world is null)
            return; // the manager logged why

        _world = world;
        World = handle;
        _folder = world.Folder;
        _extent = null;
        Root = _ecs.Create("World");
        _globalsRoot = _ecs.Create("Globals", Root);
        _chunksRoot = _ecs.Create("Chunks", Root);

        LoadGlobals();
        Debugging.Log.Info($"World {world.Name}: {_globals.Count} global chunk(s), chunk size {world.ChunkSize} m.");
    }

    /// <summary>One frame of streaming: this frame's asset and gem changes, finished loads, then what the cameras want.</summary>
    public void Update(in Frame frame)
    {
        _frame++;
        foreach (Event e in frame.Events.Span)
            Apply(e);

        Pump();

        if (_world is null)
        {
            Stats = default;
            return;
        }

        Want();
        foreach ((int X, int Y, int Z) c in _wanted)
            _unneededSince.Remove(c);

        BeginNearest();

        _scratch.Clear();
        foreach ((int X, int Y, int Z) c in _chunks.Keys)
        {
            if (!_wanted.Contains(c))
                _scratch.Add(c);
        }

        foreach ((int X, int Y, int Z) c in _loading.Keys)
        {
            if (!_wanted.Contains(c))
                _scratch.Add(c);
        }

        foreach ((int X, int Y, int Z) c in _ready.Keys)
        {
            if (!_wanted.Contains(c))
                _scratch.Add(c);
        }

        int pending = 0;
        foreach ((int X, int Y, int Z) c in _scratch)
        {
            if (!_unneededSince.TryGetValue(c, out long since))
                _unneededSince[c] = since = _frame;

            if (_frame - since >= UnloadDelayFrames)
                Unload(c);
            else
                pending++;
        }

        int entities = 0;
        foreach ((_, _, int n) in _chunks.Values)
            entities += n;

        Stats = new StreamingStats(_cameraCount, _wanted.Count, _active.Count, _chunks.Count, _loading.Count + _ready.Count, pending, entities);
    }

    /// <summary>
    /// The cubes one camera needs: the one it stands in (active), every cube within one chunk size of it wherever it
    /// looks (so turning never waits on a load), and every cube within <paramref name="viewDist"/> metres that its
    /// frustum touches. Pure, for tests. <paramref name="frustum"/> is camera-relative, as <see cref="Camera.Frustum"/>
    /// gives; <paramref name="viewDist"/> is finite (the caller cuts it to the world's extent).
    /// </summary>
    public static void Select(float chunkSize, Vector3 camera, in Frustum frustum, float viewDist, HashSet<(int X, int Y, int Z)> wanted, HashSet<(int X, int Y, int Z)>? active = null)
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

    public static (int X, int Y, int Z) Coordinate(Vector3 position, float chunkSize)
    {
        return ((int)MathF.Floor(position.X / chunkSize), (int)MathF.Floor(position.Y / chunkSize), (int)MathF.Floor(position.Z / chunkSize));
    }

    /// <summary>A changed world reopens, a changed chunk reloads, a removed one goes; new gems may bring component types.</summary>
    private void Apply(Event e)
    {
        if (e.Type is EventType.AssetAdded or EventType.AssetRemoved)
            _extent = null;

        switch (e.Type)
        {
            case EventType.GemsChanged:
                _types = null;
                lock (_warned)
                    _warned.Clear();
                Restart();
                break;
            case EventType.AssetRemoved when e.Id == World.Id:
                Close();
                break;
            case EventType.AssetRemoved:
                ForgetChunk(e.Id);
                break;
            case EventType.AssetModified when e.Id == World.Id:
                Open(World);
                break;
            case EventType.AssetModified when _globals.Any(g => g.Id == e.Id):
                ReloadGlobals();
                break;
            case EventType.AssetModified:
                _failed.Remove(e.Id);
                ForgetChunk(e.Id); // wanted again next frame, so it reloads with the new content
                break;
        }
    }

    /// <summary>
    /// Drops every chunk not spawned yet: its components may be of a gem that just went. The cubes are still wanted,
    /// so they load again next frame with the types there are now.
    /// </summary>
    private void Restart()
    {
        foreach ((_, _, CancellationTokenSource cancel) in _loading.Values)
            cancel.Cancel();

        _loading.Clear();
        _ready.Clear();
    }

    private void Close()
    {
        Restart();
        _chunks.Clear();
        _unneededSince.Clear();
        _wanted.Clear();
        _active.Clear();
        _eyes.Clear();
        _globals.Clear();

        if (Root.IsValid && _ecs.IsAlive(Root))
            _ecs.Destroy(Root);

        Root = _globalsRoot = _chunksRoot = Handle.None;
        _world = null;
        World = Handle<World>.None;
    }

    private void LoadGlobals()
    {
        foreach (Handle<Chunk> handle in _assets.FindAll<Chunk>(_folder, ".chunk"))
        {
            if (Chunk.ParseCoordinate(Path.GetFileNameWithoutExtension(_assets.PathOf(handle.Id) ?? "")) is not null)
                continue;

            _globals.Add(handle);
            if (_assets.Load(handle) is { } chunk)
                Spawn(Prepare(chunk), _globalsRoot);
        }
    }

    private void ReloadGlobals()
    {
        foreach (Handle child in _ecs.GetChildren(_globalsRoot))
            _ecs.Destroy(child);

        _globals.Clear();
        LoadGlobals();
    }

    /// <summary>Drops a streamed chunk by asset id, loaded, ready or loading; a no-op for other ids.</summary>
    private void ForgetChunk(ulong id)
    {
        foreach (((int X, int Y, int Z) c, (Handle<Chunk> asset, _, _)) in _chunks)
        {
            if (asset.Id != id)
                continue;

            Unload(c);
            return;
        }

        foreach (((int X, int Y, int Z) c, (Handle<Chunk> asset, _)) in _ready)
        {
            if (asset.Id != id)
                continue;

            Unload(c);
            return;
        }

        foreach (((int X, int Y, int Z) c, (Handle<Chunk> asset, _, _)) in _loading)
        {
            if (asset.Id != id)
                continue;

            Unload(c);
            return;
        }
    }

    private void Want()
    {
        _wanted.Clear();
        _active.Clear();
        _eyes.Clear();

        float size = _world!.ChunkSize;
        float viewDist = MathF.Max(0f, _project.Settings.Render.ViewDist);
        ((int X, int Y, int Z) Min, (int X, int Y, int Z) Max) extent = _extent ??= Extent();
        Vector3 min = new Vector3(extent.Min.X, extent.Min.Y, extent.Min.Z) * size;
        Vector3 max = (new Vector3(extent.Max.X, extent.Max.Y, extent.Max.Z) + Vector3.One) * size;

        int cameras = 0;
        _cameras.Run((ReadOnlySpan<Handle> _, Span<Camera> cams, Span<WorldTransform> worlds) =>
        {
            for (int i = 0; i < cams.Length; i++)
            {
                Frustum frustum = cams[i].Frustum(worlds[i].Value, StreamingAspect);
                Vector3 eye = worlds[i].Value.Translation;

                // No farther than the world's farthest chunk: an infinite (or huge) view distance walks only cubes there are.
                float farthest = Vector3.Max(Vector3.Abs(min - eye), Vector3.Abs(max - eye)).Length();
                Select(size, eye, frustum, MathF.Min(viewDist, farthest), _wanted, _active);
                _eyes.Add(eye);
                cameras++;
            }
        });

        _cameraCount = cameras;
    }

    /// <summary>The lowest and highest coordinates among the world's chunk files; the origin's cube when it has none.</summary>
    private ((int X, int Y, int Z) Min, (int X, int Y, int Z) Max) Extent()
    {
        (int X, int Y, int Z) min = (int.MaxValue, int.MaxValue, int.MaxValue), max = (int.MinValue, int.MinValue, int.MinValue);
        foreach (Handle<Chunk> handle in _assets.FindAll<Chunk>(_folder, ".chunk"))
        {
            if (Chunk.ParseCoordinate(Path.GetFileNameWithoutExtension(_assets.PathOf(handle.Id) ?? "")) is not { } c)
                continue;

            min = (Math.Min(min.X, c.X), Math.Min(min.Y, c.Y), Math.Min(min.Z, c.Z));
            max = (Math.Max(max.X, c.X), Math.Max(max.Y, c.Y), Math.Max(max.Z, c.Z));
        }

        return min.X > max.X ? ((0, 0, 0), (0, 0, 0)) : (min, max);
    }

    /// <summary>Starts the nearest wanted cubes that are not in hand yet, while fewer than <see cref="MaxLoads"/> are loading.</summary>
    private void BeginNearest()
    {
        if (_loading.Count >= MaxLoads)
            return;

        _scratch.Clear();
        foreach ((int X, int Y, int Z) c in _wanted)
        {
            if (!_chunks.ContainsKey(c) && !_loading.ContainsKey(c) && !_ready.ContainsKey(c))
                _scratch.Add(c);
        }

        _scratch.Sort(_nearer);
        for (int i = 0; i < _scratch.Count && _loading.Count < MaxLoads; i++)
            Begin(_scratch[i]);
    }

    private void Begin((int X, int Y, int Z) c)
    {
        Handle<Chunk> handle = _assets.Find<Chunk>($"{_folder}/{Chunk.FileName(c.X, c.Y, c.Z)}");
        if (!handle.IsValid || _failed.Contains(handle.Id))
            return;

        CancellationTokenSource cancel = new();
        _loading[c] = (handle, Load(handle, cancel.Token), cancel);
    }

    /// <summary>
    /// The chunk, read and prepared on a worker, done only once every model, material and texture its renderers name
    /// has been loaded there. They land in the asset manager's pools, so the renderer's own loads in the frame the chunk
    /// spawns find them there instead of reading and importing on the main thread; one already pooled costs nothing.
    /// </summary>
    private Task<Prepared[]?> Load(Handle<Chunk> handle, CancellationToken cancel)
    {
        return Task.Run(async () =>
        {
            if (_assets.Load(handle) is not { } chunk)
                return null;

            Prepared[] entities = Prepare(chunk);

            HashSet<ulong> models = [], materials = [];
            foreach (Prepared entity in entities)
            {
                foreach ((_, object value) in entity.Components)
                {
                    if (value is Renderer renderer)
                        Referenced(renderer, models, materials);
                }
            }

            await Task.WhenAll([
                .. models.Select(id => Task.Run(() => _assets.Load(new Handle<Model>(id)))),
                .. materials.Select(id => Task.Run(() => LoadMaterial(new Handle<Material>(id))))]);

            return entities;
        }, cancel);
    }

    private static void Referenced(in Renderer renderer, HashSet<ulong> models, HashSet<ulong> materials)
    {
        if (renderer.Model.IsValid)
            models.Add(renderer.Model.Id);

        for (int i = 0; i < MaterialSlots.Capacity; i++)
        {
            if (renderer.Materials[i].IsValid)
                materials.Add(renderer.Materials[i].Id);
        }
    }

    /// <summary>The material and the textures it names, each on a worker of its own.</summary>
    private Task LoadMaterial(Handle<Material> handle)
    {
        if (_assets.Load(handle) is not { } material)
            return Task.CompletedTask;

        return Task.WhenAll(material.Params.Values
            .Where(p => p.Texture.IsValid && !RenderTexture.IsAt(_assets.PathOf(p.Texture.Id))) // nothing to decode: a camera draws it
            .Select(p => Task.Run(() => _assets.Load(p.Texture))));
    }

    /// <summary>Finished loads wait as ready; the nearest spawn until this frame's <see cref="SpawnBudgetMs"/> is spent. Cancelled or failed loads are dropped.</summary>
    private void Pump()
    {
        _scratch.Clear();
        foreach (((int X, int Y, int Z) c, (_, Task<Prepared[]?> task, _)) in _loading)
        {
            if (task.IsCompleted)
                _scratch.Add(c);
        }

        foreach ((int X, int Y, int Z) c in _scratch)
        {
            (Handle<Chunk> asset, Task<Prepared[]?> task, CancellationTokenSource cancel) = _loading[c];
            _loading.Remove(c);
            Prepared[]? entities = task.IsCompletedSuccessfully ? task.Result : null;
            cancel.Dispose();

            if (entities is not null)
                _ready[c] = (asset, entities);
            else if (!task.IsCanceled)
                _failed.Add(asset.Id);
        }

        long started = Stopwatch.GetTimestamp();
        while (_ready.Count > 0)
        {
            (int X, int Y, int Z) c = _ready.Keys.MinBy(Distance);
            _ready.Remove(c, out (Handle<Chunk> Asset, Prepared[] Entities) ready);

            Handle root = _ecs.Create($"C{c.X}_{c.Y}_{c.Z}", _chunksRoot);
            int entities = Spawn(ready.Entities, root);
            _chunks[c] = (ready.Asset, root, entities);
            Debugging.Log.Verbose($"Chunk {c.X}_{c.Y}_{c.Z} loaded: {entities} entities.");

            if (Stopwatch.GetElapsedTime(started).TotalMilliseconds >= SpawnBudgetMs)
                break;
        }
    }

    /// <summary>How far a cube is from the nearest camera, squared; the cube a camera stands in comes before all.</summary>
    private float Distance((int X, int Y, int Z) c)
    {
        if (_active.Contains(c) || _world is null)
            return -1f;

        Vector3 centre = (new Vector3(c.X, c.Y, c.Z) + new Vector3(0.5f)) * _world.ChunkSize;
        float nearest = float.MaxValue;
        foreach (Vector3 eye in _eyes)
            nearest = MathF.Min(nearest, Vector3.DistanceSquared(centre, eye));

        return nearest;
    }

    private void Unload((int X, int Y, int Z) c)
    {
        _unneededSince.Remove(c);
        _ready.Remove(c);
        if (_loading.Remove(c, out (Handle<Chunk> Asset, Task<Prepared[]?> Task, CancellationTokenSource Cancel) load))
            load.Cancel.Cancel();

        if (!_chunks.Remove(c, out (Handle<Chunk> Asset, Handle Root, int Entities) loaded))
            return;

        if (_ecs.IsAlive(loaded.Root))
            _ecs.Destroy(loaded.Root);

        Debugging.Log.Verbose($"Chunk {c.X}_{c.Y}_{c.Z} unloaded.");
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
        ComponentTypes types = _types ??= new ComponentTypes(Scan());
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
        entities.Add(new Prepared(parent, entity.Name, entity.Id, tags, [.. components]));
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

        Warn(name, $"{file}: {name} names {found.Count} component types ({string.Join(", ", found.Select(t => t.FullName))}); use the full name.");
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

    private static Dictionary<string, List<Type>> Scan()
    {
        // Only Core and what references it can declare an IComponent; skipping the framework is most of the time saved.
        Assembly core = typeof(IComponent).Assembly;
        string? coreName = core.GetName().Name;
        Dictionary<string, List<Type>> found = new(StringComparer.OrdinalIgnoreCase);
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic || (assembly != core && !assembly.GetReferencedAssemblies().Any(r => r.Name == coreName)))
                continue;

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t is not null).ToArray()!;
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

    public void Dispose()
    {
        Close();
        _cameras.Dispose();
    }

    /// <summary>One entity of a chunk as the main thread spawns it; <see cref="Parent"/> indexes an earlier entity, or is -1 for the chunk's root.</summary>
    private readonly record struct Prepared(int Parent, string? Name, ulong Id, Tags? Tags, (Action<IEcs, Handle, object> Set, object Value)[] Components);

    /// <summary>Every <see cref="IComponent"/> by name, and the generic Set&lt;T&gt; for each, as one snapshot workers share.</summary>
    private sealed class ComponentTypes(Dictionary<string, List<Type>> byName)
    {
        private readonly ConcurrentDictionary<Type, Action<IEcs, Handle, object>> _setters = new();

        public Dictionary<string, List<Type>> ByName { get; } = byName;

        /// <summary>Throws ArgumentException for a struct that is not unmanaged.</summary>
        public Action<IEcs, Handle, object> Setter(Type type)
        {
            return _setters.GetOrAdd(type, static t => typeof(StreamingSystem)
                .GetMethod(nameof(SetBoxed), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(t)
                .CreateDelegate<Action<IEcs, Handle, object>>());
        }
    }
}
