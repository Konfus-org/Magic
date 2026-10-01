using Magic.Attributes;
using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Events;
using Magic.Contexts.Settings;
using Magic.Interfaces;
using Magic.Utils;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Magic.Services;

/// <summary>What one asset type's pool holds, for the debug window and logs. Bytes and Budget are in bytes.</summary>
public readonly record struct AssetPoolStats(string Type, int Count, long Bytes, long Budget, long Hits, long Misses);

/// <summary>
/// Knows where every asset is and loads one on request. Every file under a watched folder (Resources, Assets, and
/// whatever <see cref="AddFolder"/> adds) is an asset, and its <c>.meta</c> sidecar (written when missing) holds the
/// id a <see cref="Handle{T}"/> carries. What it loads stays in its type's pool, within that type's budget in
/// <see cref="AssetSettings"/>, least recently used first out, so a second Load is free and returns the same object.
/// The pool is only that: whoever needs an asset to outlive it keeps its own reference. The folders are watched:
/// <see cref="ProcessChanges"/>, called by the frame loop, applies settled changes to the index, evicts what changed
/// and publishes <see cref="EventType.AssetAdded"/>, <see cref="EventType.AssetModified"/>,
/// <see cref="EventType.AssetMoved"/> and <see cref="EventType.AssetRemoved"/>.
/// </summary>
public sealed class Assets : IDisposable
{
    private const long Megabyte = 1024 * 1024;

    private readonly Project _project;
    private readonly IFileSystem _files;
    private readonly Events _events;
    private readonly Container _container;
    private readonly IDisposable _gemsChanged;
    private readonly ChangeQueue _changes = new();
    private readonly List<(string Root, IDisposable Watcher)> _folders = [];
    private readonly Lock _lock = new(); // the index and the pools are used from worker threads (LoadAsync, streaming)
    private readonly Dictionary<ulong, string> _pathById = [];
    private readonly Dictionary<string, ulong> _idByPath = new(StringComparer.OrdinalIgnoreCase); // full paths
    private readonly Dictionary<Type, Pool> _pools = [];
    private long _clock; // counts Loads, for least recently used

    public Assets(Project project, IFileSystem files, Events events, Container container)
    {
        _project = project;
        _files = files;
        _events = events;
        _container = container;
        _gemsChanged = events.Watch(EventType.GemsChanged, _ => OnGemsChanged());

        if (files.DirectoryExists(project.Resources))
            AddFolder(project.Resources);
        if (files.DirectoryExists(project.Assets))
            AddFolder(project.Assets);
    }

    /// <summary>Indexes and watches another folder; every file under it is an asset. Main thread.</summary>
    public void AddFolder(string folder)
    {
        string root = _files.FullPath(folder);
        if (!_files.DirectoryExists(root))
            throw new DirectoryNotFoundException(root);
        if (_folders.Any(f => string.Equals(f.Root, root, StringComparison.OrdinalIgnoreCase)))
            return;

        _folders.Add((root, _files.Watch(root, null, _changes.Add, recursive: true)));

        Result<string[]> listing = _files.ReadDirectoryRecursive(root);
        if (listing.Failed)
        {
            Debugging.Log.Warn($"Could not index assets in {root}: {listing.Message}");
            return;
        }

        foreach (string path in listing.Payload)
        {
            if (_files.FileExists(path) && !IsSidecar(path))
                Index(path, publish: false);
        }
    }

    /// <summary>
    /// The handle of the asset at <paramref name="path"/>, relative to any watched folder (forward slashes, like
    /// <see cref="Asset.Path"/>); <see cref="Handle{T}.None"/> when no such file is indexed. For code that starts from a
    /// name; files reference each other by id.
    /// </summary>
    public Handle<T> Find<T>(string path) where T : Asset
    {
        lock (_lock)
        {
            foreach ((string root, _) in _folders)
            {
                if (_idByPath.TryGetValue(_files.FullPath(_files.Combine(root, path)), out ulong id)) // FullPath normalises the slashes
                    return new Handle<T>(id);
            }
        }

        return Handle<T>.None;
    }

    /// <summary>
    /// The handles of every indexed file directly in <paramref name="folder"/> (relative to any watched folder, forward
    /// slashes) with the given extension, in path order. For code that starts from a folder, like a world's chunks.
    /// </summary>
    public Handle<T>[] FindAll<T>(string folder, string extension) where T : Asset
    {
        List<(string Path, ulong Id)> found = [];

        lock (_lock)
        {
            foreach ((string root, _) in _folders)
            {
                string full = _files.FullPath(_files.Combine(root, folder));
                foreach ((string path, ulong id) in _idByPath)
                {
                    if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(Path.GetDirectoryName(path), full, StringComparison.OrdinalIgnoreCase))
                        found.Add((path, id));
                }
            }
        }

        return [.. found.OrderBy(f => f.Path, StringComparer.Ordinal).Select(f => new Handle<T>(f.Id))];
    }

    /// <summary>The relative path of an asset, or null for an id nothing has.</summary>
    public string? PathOf(ulong id)
    {
        return FullPathOf(id) is { } path ? Relative(path) : null;
    }

    /// <summary>
    /// The asset; null when it cannot be loaded (logged once, until its file changes). From its type's pool when it is
    /// there, else read from disk on the calling thread and pooled, so every Load of it returns the same object: it is
    /// shared and read-only. A type whose budget is 0 is never pooled, and every Load of it reads the file again. Any
    /// thread; loads of one id at once read it once.
    /// </summary>
    public T? Load<T>(Handle<T> handle) where T : Asset
    {
        long budget = BudgetOf(typeof(T));
        if (budget == 0)
        {
            lock (_lock)
                _pools.Remove(typeof(T)); // the budget went to 0 while it held some

            return (T?)LoadFile(handle).Asset;
        }

        Pool? pool;
        Entry? entry;
        bool hit;
        lock (_lock)
        {
            if (!_pools.TryGetValue(typeof(T), out pool))
                _pools[typeof(T)] = pool = new Pool();

            hit = pool.Entries.TryGetValue(handle.Id, out entry);
            if (!hit)
                pool.Entries[handle.Id] = entry = new Entry(new Lazy<(Asset?, long)>(() => LoadFile(handle)));

            entry!.Used = ++_clock;
            if (hit)
                pool.Hits++;
            else
                pool.Misses++;
        }

        (Asset? asset, long bytes) = entry.Value.Value; // the first caller reads; the others wait for it
        if (!hit)
            Admit(pool, handle.Id, entry, bytes, budget);

        return (T?)asset;
    }

    /// <summary><see cref="Load{T}"/> on a worker thread.</summary>
    public Task<T?> LoadAsync<T>(Handle<T> handle, CancellationToken cancellationToken = default) where T : Asset
    {
        return Task.Run(() => Load(handle), cancellationToken);
    }

    /// <summary>What every type's pool holds now, by type name.</summary>
    public AssetPoolStats[] PoolStats()
    {
        lock (_lock)
        {
            return [.. _pools
                .Select(p => new AssetPoolStats(p.Key.Name, p.Value.Entries.Count, p.Value.Bytes, BudgetOf(p.Key), p.Value.Hits, p.Value.Misses))
                .OrderBy(s => s.Type, StringComparer.Ordinal)];
        }
    }

    /// <summary>
    /// Applies every file change that has settled and publishes what happened. Called by the frame loop at the start of
    /// each frame, so the index only changes between frames.
    /// </summary>
    public void ProcessChanges()
    {
        string[] settled = _changes.TakeSettled();
        if (settled.Length == 0)
            return;

        // A sidecar change is its asset's change; a folder change is every file's under it (a moved folder reports
        // only itself). Files that exist go first, so a move claims its id before the old path is seen as gone.
        HashSet<string> present = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> missing = new(StringComparer.OrdinalIgnoreCase);
        foreach (string changed in settled)
        {
            if (IsMinted(changed))
                continue;

            string path = IsSidecar(changed) ? changed[..^".meta".Length] : changed;
            if (_files.DirectoryExists(path))
            {
                Result<string[]> listing = _files.ReadDirectoryRecursive(path);
                if (listing.Ok)
                    present.UnionWith(listing.Payload.Where(p => _files.FileExists(p) && !IsSidecar(p)));
            }
            else if (_files.FileExists(path))
                present.Add(path);
            else
                missing.UnionWith(IndexedUnder(path));
        }

        foreach (string path in present)
            Index(path, publish: true);

        foreach (string path in missing)
            Unindex(path);
    }

    /// <summary>
    /// Is this a sidecar exactly as <see cref="Mint"/> writes it, for the id already indexed beside it? Then its change
    /// is the manager's own write, not news. Judged from the file alone, so nothing has to remember what was written.
    /// </summary>
    private bool IsMinted(string path)
    {
        if (!IsSidecar(path) || _files.ReadText(path) is not { Ok: true } text)
            return false;

        lock (_lock)
        {
            return _idByPath.TryGetValue(path[..^".meta".Length], out ulong id) && text.Payload == SidecarText(id);
        }
    }

    /// <summary>The indexed path itself, or every indexed path under it when it was a folder.</summary>
    private string[] IndexedUnder(string path)
    {
        lock (_lock)
        {
            string folder = path + Path.DirectorySeparatorChar;
            return [.. _idByPath.Keys.Where(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase) || p.StartsWith(folder, StringComparison.OrdinalIgnoreCase))];
        }
    }

    private string? FullPathOf(ulong id)
    {
        lock (_lock)
        {
            return _pathById.GetValueOrDefault(id);
        }
    }

    /// <summary>
    /// The asset read from disk with what it holds in memory, or null (logged) and 0 when it cannot be loaded. Every call
    /// reads the file and returns a new object.
    /// </summary>
    private (Asset? Asset, long Bytes) LoadFile<T>(Handle<T> handle) where T : Asset
    {
        string? path = FullPathOf(handle.Id);

        try
        {
            if (path is null)
                throw new KeyNotFoundException("no asset has this id.");

            T asset = Read(Sidecar<T>(path), path, out long fileBytes);
            return (asset, asset.Bytes > 0 ? asset.Bytes : fileBytes);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            string location = path is null ? "" : $" ({Relative(path)})";
            Debugging.Log.Warn($"Failed to load {typeof(T).Name} {handle.Id}{location}: {ex.Message}");
            return (null, 0);
        }
    }

    /// <summary>
    /// Counts a newly read entry into its pool, then evicts the pool's least recently used entries while it is over
    /// <paramref name="budget"/>, never the new one: an asset bigger than its budget stays until the next miss.
    /// </summary>
    private void Admit(Pool pool, ulong id, Entry entry, long bytes, long budget)
    {
        lock (_lock)
        {
            if (!pool.Entries.TryGetValue(id, out Entry? still) || still != entry)
                return; // its file changed while it was being read: evicted, so not counted

            entry.Bytes = bytes;
            pool.Bytes += bytes;
            while (pool.Bytes > budget && pool.Oldest(except: id) is { } oldest)
                pool.Remove(oldest);
        }
    }

    /// <summary>The type's budget in bytes, from the settings as they are now: by its name, else the default.</summary>
    private long BudgetOf(Type type)
    {
        AssetSettings settings = _project.Settings.Assets;
        foreach ((string name, int megabytes) in settings.Budgets)
        {
            if (IsNamed(type, name))
                return Math.Max(0, megabytes) * Megabyte;
        }

        return Math.Max(0, settings.DefaultBudget) * Megabyte;
    }

    /// <summary>Takes every changed id out of the pools, so the next Load reads the file. Under the lock.</summary>
    private void Evict(ulong id)
    {
        foreach (Pool pool in _pools.Values)
            pool.Remove(id);
    }

    /// <summary>
    /// Gems came or went: the pools of their asset types go (they would keep an unloaded gem's assembly alive), and the
    /// budget names are checked against the asset types there are now.
    /// </summary>
    private void OnGemsChanged()
    {
        lock (_lock)
        {
            foreach (Type type in _pools.Keys.Where(t => t.Assembly.IsCollectible).ToArray())
                _pools.Remove(type);
        }

        Type[] types = AssetTypes();
        foreach (string name in _project.Settings.Assets.Budgets.Keys)
        {
            if (!types.Any(t => IsNamed(t, name)))
                Debugging.Log.Warn($"Assets.Budgets: no asset type is called '{name}'.");
        }
    }

    /// <summary>The sidecar deserialised as the asset type: id, version and the [MetaData] properties, plus the path.</summary>
    private T Sidecar<T>(string path) where T : Asset
    {
        Result<string> meta = _files.ReadText(path + ".meta");
        if (meta.Failed)
            throw new IOException($"could not read its sidecar. {meta.Message}");

        T asset = JsonSerializer.Deserialize<T>(meta.Payload, AssetJson.Options) ?? throw new JsonException("the sidecar is null.");
        asset.Path = Relative(path);

        return asset;
    }

    /// <summary>
    /// Fills the sidecar's asset from the file by its <see cref="AssetFormat"/>: the host reads JSON and text itself; a
    /// custom format, which is also what a type without <see cref="AssetFormatAttribute"/> has, goes to the
    /// <see cref="IAssetLoader{T}"/> a gem provides. <paramref name="fileBytes"/> is about what the file's contents take in memory.
    /// </summary>
    private T Read<T>(T asset, string path, out long fileBytes) where T : Asset
    {
        AssetFormat format = typeof(T).GetCustomAttribute<AssetFormatAttribute>()?.Format ?? AssetFormat.Custom;
        switch (format)
        {
            case AssetFormat.Json:
            {
                // The file is the same type; the sidecar's values win for what it owns.
                string json = Payload(_files.ReadText(path));
                fileBytes = json.Length * (long)sizeof(char);
                T content = JsonSerializer.Deserialize<T>(json, AssetJson.Options) ?? throw new JsonException("the file is null.");
                content.Id = asset.Id;
                content.Version = asset.Version;
                content.Path = asset.Path;
                foreach (PropertyInfo property in typeof(T).GetProperties().Where(p => p.IsDefined(typeof(MetaDataAttribute))))
                    property.SetValue(content, property.GetValue(asset));

                return content;
            }
            case AssetFormat.Text:
            {
                PropertyInfo property = typeof(T).GetProperty("Text", typeof(string))
                    ?? throw new InvalidOperationException($"{typeof(T).Name} is a text asset but has no string Text property.");
                string text = Payload(_files.ReadText(path));
                fileBytes = text.Length * (long)sizeof(char);
                property.SetValue(asset, text);

                return asset;
            }
            case AssetFormat.Custom:
            {
                IAssetLoader<T> loader = _container.Get<IAssetLoader<T>>()
                    ?? throw new InvalidOperationException($"no loaded gem provides an IAssetLoader<{typeof(T).Name}>.");
                byte[] bytes = Payload(_files.ReadBinary(path));
                fileBytes = bytes.LongLength;
                loader.Load(asset, bytes);

                return asset;
            }
            default:
                throw new InvalidOperationException($"{typeof(T).Name} has an unknown asset format, {format}.");
        }
    }

    /// <summary>
    /// Puts the file at <paramref name="path"/> in the index under its sidecar's id and, when asked, publishes what
    /// changed: added, modified, or moved here from a path that no longer exists.
    /// </summary>
    private void Index(string path, bool publish)
    {
        ulong id = ReadId(path + ".meta") ?? Mint(path + ".meta");
        if (id == 0)
            return;

        string relative = Relative(path);
        List<Event> happened = [];

        lock (_lock)
        {
            if (_idByPath.TryGetValue(path, out ulong previous) && previous != id)
            {
                // The sidecar was rewritten with another id: the old one is gone from this path.
                _pathById.Remove(previous);
                Evict(previous);
                happened.Add(new Event(EventType.AssetRemoved, Id: previous, Text: relative));
            }

            if (_pathById.TryGetValue(id, out string? elsewhere) && !string.Equals(elsewhere, path, StringComparison.OrdinalIgnoreCase))
            {
                if (_files.FileExists(elsewhere))
                {
                    Debugging.Log.Error($"{relative} and {Relative(elsewhere)} share id {id}; keeping {Relative(elsewhere)}.");
                    return;
                }

                _idByPath.Remove(elsewhere);
                happened.Add(new Event(EventType.AssetMoved, Id: id, Text: relative, OldText: Relative(elsewhere)));
            }
            else
            {
                happened.Add(new Event(elsewhere is null ? EventType.AssetAdded : EventType.AssetModified, Id: id, Text: relative));
            }

            _pathById[id] = path;
            _idByPath[path] = id;
            Evict(id); // added, written or moved: a pooled copy (or a pooled failure) is stale
        }

        if (!publish)
            return;

        foreach (Event e in happened)
            _events.Publish(e);
    }

    private void Unindex(string path)
    {
        ulong id;
        lock (_lock)
        {
            if (!_idByPath.Remove(path, out id))
                return;

            _pathById.Remove(id);
            Evict(id);
        }

        _events.Publish(new Event(EventType.AssetRemoved, Id: id, Text: Relative(path)));
    }

    private ulong? ReadId(string metaPath)
    {
        if (!_files.FileExists(metaPath))
            return null;

        Result<string> read = _files.ReadText(metaPath);
        if (read.Failed)
        {
            Debugging.Log.Warn($"Could not read {metaPath}: {read.Message}");
            return 0;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(read.Payload, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return document.RootElement.TryGetProperty("id", out JsonElement id) && id.TryGetUInt64(out ulong value) ? value : 0;
        }
        catch (JsonException ex)
        {
            Debugging.Log.Warn($"{metaPath} is not valid JSON: {ex.Message}");
            return 0;
        }
    }

    /// <summary>Gives a file without a sidecar a fresh id, so it can be referenced from now on.</summary>
    private ulong Mint(string metaPath)
    {
        ulong id;
        lock (_lock)
        {
            do
            {
                id = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
            } while (id == 0 || _pathById.ContainsKey(id));
        }

        Result write = _files.WriteText(metaPath, SidecarText(id));
        if (write.Ok)
            return id;

        Debugging.Log.Warn($"Could not write {metaPath}: {write.Message}");

        return 0;
    }

    /// <summary>The path relative to the folder it is under, forward slashes.</summary>
    private string Relative(string path)
    {
        foreach ((string root, _) in _folders)
        {
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return _files.Relative(root, path).Replace('\\', '/');
        }

        return path.Replace('\\', '/');
    }

    /// <summary>What <see cref="Mint"/> writes for <paramref name="id"/>.</summary>
    private static string SidecarText(ulong id)
    {
        return $"{{\n    \"id\": {id},\n    \"version\": 1\n}}\n";
    }

    private static TPayload Payload<TPayload>(Result<TPayload> read)
    {
        return read.Failed ? throw new IOException(read.Message) : read.Payload;
    }

    private static bool IsSidecar(string path)
    {
        return path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a budget name is the type's: its name or full name, any case.</summary>
    private static bool IsNamed(Type type, string name)
    {
        return string.Equals(type.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(type.FullName, name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Every concrete asset type in Core and the assemblies that reference it (the gems).</summary>
    private static Type[] AssetTypes()
    {
        Assembly core = typeof(Asset).Assembly;
        string? coreName = core.GetName().Name;
        List<Type> found = [];
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
                types = [.. ex.Types.Where(t => t is not null)!];
            }

            found.AddRange(types.Where(t => !t.IsAbstract && typeof(Asset).IsAssignableFrom(t)));
        }

        return [.. found];
    }

    public void Dispose()
    {
        _gemsChanged.Dispose();
        foreach ((_, IDisposable watcher) in _folders)
            watcher.Dispose();

        _folders.Clear();
        lock (_lock)
            _pools.Clear();
    }

    /// <summary>One asset type's loaded assets by id, with what they take and how often a Load found one. Used under the lock.</summary>
    private sealed class Pool
    {
        public Dictionary<ulong, Entry> Entries { get; } = [];

        /// <summary>What the counted entries hold; an entry still being read counts nothing yet.</summary>
        public long Bytes { get; set; }

        public long Hits { get; set; }

        public long Misses { get; set; }

        /// <summary>The least recently loaded id other than <paramref name="except"/>; null when there is none.</summary>
        public ulong? Oldest(ulong except)
        {
            ulong? oldest = null;
            long used = long.MaxValue;
            foreach ((ulong id, Entry entry) in Entries)
            {
                if (id != except && entry.Used < used)
                {
                    oldest = id;
                    used = entry.Used;
                }
            }

            return oldest;
        }

        public void Remove(ulong id)
        {
            if (Entries.Remove(id, out Entry? entry))
                Bytes -= entry.Bytes;
        }
    }

    /// <summary>One pooled asset: read once by whoever loads it first, null when it failed.</summary>
    private sealed class Entry(Lazy<(Asset? Asset, long Bytes)> value)
    {
        public Lazy<(Asset? Asset, long Bytes)> Value { get; } = value;

        /// <summary>What it counts for in its pool; 0 until it is admitted.</summary>
        public long Bytes { get; set; }

        /// <summary>When it was last loaded, by the manager's load count.</summary>
        public long Used { get; set; }
    }
}
