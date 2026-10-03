using Magic.Attributes;
using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Events;
using Magic.Contexts.Files;
using Magic.Contexts.Settings;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Utils;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Magic.Services;

/// <summary>
/// What one asset type's pool holds, for the debug window and logs. Bytes and Budget are in bytes.
/// </summary>
internal readonly record struct AssetPoolStats(string Type, int Count, long Bytes, long Budget, long Hits, long Misses);

/// <summary>
/// Knows where every asset is and loads one on request. Every file under a watched folder (Resources, Assets, and
/// whatever <see cref="AddFolder"/> adds) is an asset, and its <c>.meta</c> sidecar (written when missing) holds the
/// id a <see cref="Handle{T}"/> carries. What it loads stays in its type's pool, within that type's budget in
/// <see cref="AssetSettings"/>, least recently used first out, so a second Load is free and returns the same object.
/// Loading is asynchronous: a file is read without holding a thread, what takes long (a gem's import, making LODs)
/// runs on a worker, and whoever awaits continues wherever the work ended. <see cref="Load{T}"/> waits for the same
/// thing on the calling thread, which costs nothing for what is pooled.
/// The pool is only that: whoever needs an asset to outlive it keeps its own reference. The folders are watched:
/// <see cref="ProcessChanges"/>, called by the frame loop, applies settled changes to the index, evicts what changed
/// and publishes <see cref="EventType.AssetAdded"/>, <see cref="EventType.AssetModified"/>,
/// <see cref="EventType.AssetMoved"/> and <see cref="EventType.AssetRemoved"/>.
/// <para>
/// An asset may have lesser versions of itself (<see cref="Lods{T}"/>): the ones its sidecar names, else the ones the
/// type's <see cref="ILODGenerator{T}"/> makes. Those are asset files like any other, in a folder of the cache named
/// by the asset's id, a hash of its file and sidecar and the generator's version, so they are made once and made
/// again when any of those changes; the folder's manifest also keeps the same of every other asset the generator
/// loaded, so they are made again when one of those changes too. That folder is indexed as it is used and is not
/// watched. A folder made of an earlier file or version is deleted when its asset's LODs are next asked for, and
/// the folders of assets that are gone the first time any of their type's are.
/// </para>
/// </summary>
public sealed class Assets : IDisposable
{
    private const long Megabyte = 1024 * 1024;

    /// <summary>
    /// How much of its budget a pool that went over is trimmed to, so the misses after it evict nothing.
    /// </summary>
    private const double TrimmedTo = 0.9;

    private readonly Project _project;
    private readonly IFileSystem _files;
    private readonly Events _events;
    private readonly Container _container;
    private readonly Threads _threads;
    private readonly IDisposable _gemsChanged;
    private readonly FileChanges _changes = new();
    private readonly List<(string Root, IDisposable? Watcher)> _folders = [];
    private readonly Lock _lock = new(); // the index and the pools are used from worker threads
    private readonly Dictionary<ulong, string> _pathById = [];
    private readonly Dictionary<string, ulong> _idByPath = new(StringComparer.OrdinalIgnoreCase); // full paths
    private readonly Dictionary<Type, Pool> _pools = [];
    private readonly HashSet<(Type Type, ulong Id)> _failed = []; // whatever the budget; until the file or the gems change
    private readonly ConcurrentDictionary<Type, Func<Assets, ulong, CancellationToken, Task<Asset?>>> _loads = new(); // LoadOneAsync by run-time type
    private readonly ConcurrentDictionary<Type, Func<Assets, ulong, CancellationToken, Task<Asset[]>>> _lodLoads = new(); // LoadLodsAsync by run-time type
    private readonly ConcurrentDictionary<(Type Type, ulong Id), Lazy<Task<Array>>> _lods = new(); // found once; until the file or the gems change
    private readonly ConcurrentDictionary<(Type Type, ulong Id), ulong[]> _lodDependencies = new(); // the other assets each one's generated LODs were made from
    private readonly ConcurrentDictionary<(Type Type, ulong Id), Lazy<Task<string?>>> _stamps = new(); // hashed once; until the file or the gems change
    private readonly ConcurrentDictionary<Type, Func<Assets, ulong, CancellationToken, Task<string?>>> _stampsOf = new(); // StampAsync by run-time type
    private readonly ConcurrentDictionary<string, Type?> _typesByName = new(); // asset types by full name, as a LOD manifest names them
    private readonly ConcurrentDictionary<Type, bool> _swept = new(); // types whose cache has been cleared of assets that are gone
    private readonly AsyncLocal<ConcurrentDictionary<(Type Type, ulong Id), bool>?> _generatorReads = new(); // what the generator running in this flow asked for
    private readonly string _lodCache;
    private long _loadCount; // counts Loads, for least recently used

    internal Assets(Project project, IFileSystem files, Events events, Container container, Threads threads)
    {
        _project = project;
        _files = files;
        _events = events;
        _container = container;
        _threads = threads;
        _gemsChanged = events.Watch(EventType.GemsChanged, _ => OnGemsChanged());

        // Generated LODs are assets of this root; nothing but this class writes there, so it is neither listed nor watched.
        _lodCache = files.FullPath(files.Combine(project.Cache, "Lods"));
        _folders.Add((_lodCache, null));

        if (files.DirectoryExists(project.Resources))
            AddFolder(project.Resources);
        if (files.DirectoryExists(project.Assets))
            AddFolder(project.Assets);
    }

    public void Dispose()
    {
        _gemsChanged.Dispose();
        foreach ((_, IDisposable? watcher) in _folders)
            watcher?.Dispose();

        _folders.Clear();
        lock (_lock)
            _pools.Clear();
    }

    /// <summary>
    /// Indexes and watches another folder; every file under it is an asset. Main thread.
    /// </summary>
    public void AddFolder(string folder)
    {
        string root = _files.FullPath(folder);
        if (!_files.DirectoryExists(root))
            throw new DirectoryNotFoundException(root);
        if (_folders.Any(folder => string.Equals(folder.Root, root, StringComparison.OrdinalIgnoreCase)))
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
    /// slashes) with the given extension, in path order. For code that starts from a folder, like a domain's chunks.
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
                        && string.Equals(_files.Parent(path), full, StringComparison.OrdinalIgnoreCase))
                        found.Add((path, id));
                }
            }
        }

        return [.. found.OrderBy(asset => asset.Path, StringComparer.Ordinal).Select(asset => new Handle<T>(asset.Id))];
    }

    /// <summary>
    /// The relative path of an asset, or null for an id nothing has.
    /// </summary>
    public string? PathOf(ulong id)
    {
        return FullPathOf(id) is { } path ? Relative(path) : null;
    }

    /// <summary>
    /// The asset; null when it cannot be loaded (logged once, and not tried again until its file or the gems change).
    /// From its type's pool when it is there, else read from disk and pooled, so every Load of it returns the same
    /// object: it is shared and read-only. A type whose budget is 0 is never pooled, and every Load of it reads the
    /// file again. With <paramref name="dependencies"/>, every asset it names (the handles <c>ValuesOf</c> finds in
    /// it) is loaded too, and what those name, each into its own pool. The calling thread waits for all of it, which
    /// is nothing for what is pooled; whatever may take long is better awaited (<see cref="LoadAsync{T}"/>). Any thread.
    /// </summary>
    public T? Load<T>(Handle<T> handle, bool dependencies = false) where T : Asset
    {
        return LoadAsync(handle, dependencies).GetAwaiter().GetResult();
    }

    /// <summary>
    /// <see cref="Load{T}"/> without a thread waiting: the file is read asynchronously and imported on a worker, and
    /// with <paramref name="dependencies"/> every one of those loads at once. Loads of one id at once read it once.
    /// Cancelling <paramref name="cancel"/> throws <see cref="OperationCanceledException"/> here, and stops the read
    /// itself once nobody else waits for it. <paramref name="progress"/> is told the share, 0 to 1, of the assets
    /// found so far that are loaded, which can fall back as more are found, and 1 at the end.
    /// </summary>
    public async Task<T?> LoadAsync<T>(Handle<T> handle, bool dependencies = false, IProgress<float>? progress = null, CancellationToken cancel = default) where T : Asset
    {
        T? asset = await LoadOneAsync(handle, cancel).ConfigureAwait(false);
        if (dependencies && asset is not null)
            await LoadNamedAsync([asset], new() { [(typeof(T), handle.Id)] = true }, new StrongBox<int>(1), progress, cancel).ConfigureAwait(false);

        progress?.Report(1f);

        return asset;
    }

    /// <summary>
    /// Loads every asset <paramref name="holders"/> name, and what those name, all at once and each once however many
    /// name it. For what is not an asset but has handles to some: a chunk's components.
    /// <paramref name="progress"/> is told what <see cref="LoadAsync{T}"/> tells.
    /// </summary>
    public async Task LoadDependenciesAsync(IEnumerable<object> holders, IProgress<float>? progress = null, CancellationToken cancel = default)
    {
        await LoadNamedAsync(holders, new(), new StrongBox<int>(), progress, cancel).ConfigureAwait(false);
        progress?.Report(1f);
    }

    /// <summary>
    /// The asset's lesser versions, the one with the highest threshold first: those its sidecar names
    /// (<see cref="Asset.Lods"/>), else those the type's <see cref="ILODGenerator{T}"/> made of it, from the cache
    /// when they are there and made first when they are not, while the calling thread waits; that can take long, so
    /// whoever cannot wait awaits <see cref="LodsAsync{T}"/>. None when it has neither, cannot be loaded, or is a
    /// generated one itself. Found once per asset, until its file or the gems change. Any thread.
    /// </summary>
    public (float Threshold, Handle<T> Asset)[] Lods<T>(Handle<T> handle) where T : Asset
    {
        return LodsAsync(handle).GetAwaiter().GetResult();
    }

    /// <summary>
    /// <see cref="Lods{T}"/> without a thread waiting: they are found, and made when they have to be, on a worker.
    /// The generator is stopped by the <paramref name="cancel"/> of whoever asked first; anyone else waiting then
    /// has them found again. It is the <paramref name="progress"/> of whoever asked first, too, that the generator
    /// tells how far it is; everyone's is told 1 at the end.
    /// </summary>
    public async Task<(float Threshold, Handle<T> Asset)[]> LodsAsync<T>(Handle<T> handle, IProgress<float>? progress = null, CancellationToken cancel = default) where T : Asset
    {
        (Type, ulong) key = (typeof(T), handle.Id);
        _generatorReads.Value?.TryAdd(key, true);
        while (true)
        {
            Lazy<Task<Array>> found = _lods.GetOrAdd(key, _ => new Lazy<Task<Array>>(() => _threads.InvokeAsync(ThreadId.Worker, cancel => FindLodsAsync(handle, progress, cancel), cancel).Unwrap()));
            try
            {
                (float, Handle<T>)[] lods = ((float, Handle<T>)[])await found.Value.WaitAsync(cancel).ConfigureAwait(false);
                progress?.Report(1f);

                return lods;
            }
            catch (OperationCanceledException) when (found.Value.IsCanceled)
            {
                _lods.TryRemove(new KeyValuePair<(Type, ulong), Lazy<Task<Array>>>(key, found));
                cancel.ThrowIfCancellationRequested();
            }
        }
    }

    /// <summary>
    /// Whether the asset could not be loaded, and so will not be tried again until its file or the gems change.
    /// </summary>
    public bool HasFailed<T>(Handle<T> handle) where T : Asset
    {
        lock (_lock)
        {
            return _failed.Contains((typeof(T), handle.Id));
        }
    }

    /// <summary>
    /// What every type's pool holds now, by type name.
    /// </summary>
    internal AssetPoolStats[] PoolStats()
    {
        lock (_lock)
        {
            return [.. _pools
                .Select(pool => new AssetPoolStats(pool.Key.Name, pool.Value.Entries.Count, pool.Value.Bytes, BudgetOf(pool.Key), pool.Value.Hits, pool.Value.Misses))
                .OrderBy(stats => stats.Type, StringComparer.Ordinal)];
        }
    }

    /// <summary>
    /// The type's budget in bytes, as the settings have it now.
    /// </summary>
    internal long BudgetOf<T>() where T : Asset
    {
        return BudgetOf(typeof(T));
    }

    /// <summary>
    /// What the asset counts for in its pool, in bytes; 0 when it is not pooled.
    /// </summary>
    internal long BytesOf<T>(Handle<T> handle) where T : Asset
    {
        lock (_lock)
        {
            return _pools.TryGetValue(typeof(T), out Pool? pool) && pool.Entries.TryGetValue(handle.Id, out Entry? entry) ? entry.Bytes : 0;
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
                    present.UnionWith(listing.Payload.Where(file => _files.FileExists(file) && !IsSidecar(file)));
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

    /// <summary>
    /// The indexed path itself, or every indexed path under it when it was a folder.
    /// </summary>
    private string[] IndexedUnder(string path)
    {
        lock (_lock)
        {
            return [.. _idByPath.Keys.Where(known => _files.IsUnder(path, known))];
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
    /// What <see cref="LodsAsync{T}"/> answers the first time it is asked.
    /// </summary>
    private async Task<Array> FindLodsAsync<T>(Handle<T> handle, IProgress<float>? progress, CancellationToken cancel) where T : Asset
    {
        (float, Handle<T>)[] none = [];
        string? path = FullPathOf(handle.Id);
        if (path is null || _files.IsUnder(_lodCache, path))
            return none;

        // The sidecar says whether it has its own, and the cache is keyed by the file's bytes: the asset itself is
        // only loaded when its LODs have to be made, so asking after a far chunk's stand-in does not read the chunk.
        string folder = "", manifestPath = "";
        LodManifest? manifest = null;
        try
        {
            Dictionary<float, ulong> authored = (await SidecarAsync<T>(path, cancel).ConfigureAwait(false)).Lods;
            if (authored.Count > 0)
                return authored.OrderByDescending(lod => lod.Key).Select(lod => (lod.Key, new Handle<T>(lod.Value))).ToArray();

            if (!_container.TryGet(out ILODGenerator<T>? generator) || await StampAsync(handle, cancel).ConfigureAwait(false) is not { } stamp)
                return none;

            string name = $"{handle.Id:x16}-{stamp}";
            folder = _files.Combine(_lodCache, typeof(T).Name, name);
            manifestPath = _files.Combine(folder, "lods.json");
            DeleteStale(typeof(T), handle.Id, name);

            manifest = await ReadManifestAsync(manifestPath, cancel).ConfigureAwait(false);
            if (manifest is not null && !await IsCurrentAsync(manifest, cancel).ConfigureAwait(false))
                manifest = null;

            if (manifest is null)
            {
                if (await LoadOneAsync(handle, cancel).ConfigureAwait(false) is not { } asset)
                    return none;

                // Whatever is there is half made, or made of other assets as they were: nothing of it may be left to index.
                Forget(folder);

                long started = Stopwatch.GetTimestamp();
                ConcurrentDictionary<(Type Type, ulong Id), bool>? outer = _generatorReads.Value;
                ConcurrentDictionary<(Type Type, ulong Id), bool> reads = new();
                _generatorReads.Value = reads;
                Result<Dictionary<float, string>> generated;
                try
                {
                    generated = await generator.GenerateAsync(asset, folder, progress, cancel).ConfigureAwait(false);
                }
                finally
                {
                    _generatorReads.Value = outer;
                }

                if (generated.Failed)
                {
                    Debugging.Log.Warn($"Could not generate the LODs of {typeof(T).Name} {handle.Id} ({asset.Path}): {generated.Message}");
                    return none;
                }

                // The manifest is what says they are all there, so it is written last: a cancelled run leaves none.
                reads.TryRemove((typeof(T), handle.Id), out _);
                manifest = new LodManifest(generated.Payload, await DependenciesAsync(reads.Keys, cancel).ConfigureAwait(false));
                Result written = await _files.WriteTextAsync(manifestPath, JsonSerializer.Serialize(manifest), cancel).ConfigureAwait(false);
                if (written.Failed)
                    Debugging.Log.Warn($"Could not write {manifestPath}: {written.Message}");

                Debugging.Log.Info($"Generated {manifest.Lods.Count} LOD(s) of {typeof(T).Name} {handle.Id} ({asset.Path}): {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms.");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            Debugging.Log.Warn($"Could not find the LODs of {typeof(T).Name} {handle.Id} ({Relative(path)}): {ex.Message}");
            return none;
        }

        _lodDependencies[(typeof(T), handle.Id)] = [.. manifest.Dependencies.Select(dependency => dependency.Id)];
        Dictionary<float, string> names = manifest.Lods;
        if (names.Count == 0)
            return none;

        // Everything in the folder is an asset: the LODs, and what they name in turn.
        if (_files.ReadDirectory(folder) is { Ok: true } listing)
        {
            foreach (string file in listing.Payload)
            {
                string full = _files.FullPath(file);
                if (_files.FileExists(full) && !IsSidecar(full) && !string.Equals(full, _files.FullPath(manifestPath), StringComparison.OrdinalIgnoreCase))
                    Index(full, publish: false);
            }
        }

        List<(float, Handle<T>)> lods = [];
        lock (_lock)
        {
            foreach ((float threshold, string name) in names.OrderByDescending(lod => lod.Key))
            {
                if (_idByPath.TryGetValue(_files.FullPath(_files.Combine(folder, name)), out ulong id))
                    lods.Add((threshold, new Handle<T>(id)));
            }
        }

        return lods.ToArray();
    }

    /// <summary>
    /// What an asset's generated LODs are made from, as text: a hash of its file and its sidecar, then the version
    /// of its type's <see cref="ILODGenerator{T}"/> when there is one. Null when it has no file or that cannot be
    /// read. Hashed once per asset, until its file or the gems change.
    /// </summary>
    private async Task<string?> StampAsync<T>(Handle<T> handle, CancellationToken cancel) where T : Asset
    {
        Lazy<Task<string?>> stamp = _stamps.GetOrAdd((typeof(T), handle.Id), _ => new Lazy<Task<string?>>(() => HashAsync<T>(handle.Id)));

        return await stamp.Value.WaitAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// What <see cref="StampAsync{T}(Handle{T}, CancellationToken)"/> answers the first time it is asked. Everyone
    /// asking shares it, so nobody's cancelling stops it.
    /// </summary>
    private async Task<string?> HashAsync<T>(ulong id) where T : Asset
    {
        // Streamed by the file system: a model is megabytes, and holding every one hashed would be most of the garbage.
        if (FullPathOf(id) is not { } path || await _files.HashAsync([path, path + ".meta"]).ConfigureAwait(false) is not { Ok: true, Payload: var text })
            return null;

        return _container.TryGet(out ILODGenerator<T>? generator) ? $"{text}-{generator.Version}" : text;
    }

    /// <summary>
    /// <see cref="StampAsync{T}(Handle{T}, CancellationToken)"/> for a type known only at run time.
    /// </summary>
    private Task<string?> StampAsync(Type type, ulong id, CancellationToken cancel)
    {
        return _stampsOf.GetOrAdd(type, static assetType => Method(nameof(StampBoxedAsync))
            .MakeGenericMethod(assetType)
            .CreateDelegate<Func<Assets, ulong, CancellationToken, Task<string?>>>())(this, id, cancel);
    }

    private static Task<string?> StampBoxedAsync<T>(Assets assets, ulong id, CancellationToken cancel) where T : Asset
    {
        return assets.StampAsync(new Handle<T>(id), cancel);
    }

    /// <summary>
    /// The manifest at <paramref name="path"/>; null when there is none or it is not one, which is a folder whose
    /// LODs have to be made.
    /// </summary>
    private async Task<LodManifest?> ReadManifestAsync(string path, CancellationToken cancel)
    {
        if (!_files.FileExists(path))
            return null;

        try
        {
            Result<LodManifest?> read = await _files.ReadAsync(path, static bytes => JsonSerializer.Deserialize<LodManifest>(bytes), cancel).ConfigureAwait(false);
            return read is { Ok: true, Payload: { Lods: not null, Dependencies: not null } manifest } ? manifest : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether every other asset the LODs were made from is still what it was then.
    /// </summary>
    private async Task<bool> IsCurrentAsync(LodManifest manifest, CancellationToken cancel)
    {
        foreach (LodDependency dependency in manifest.Dependencies)
        {
            Type? type = _typesByName.GetOrAdd(dependency.Type, static name => AssetTypes().FirstOrDefault(candidate => candidate.FullName == name));
            if (type is null || await StampAsync(type, dependency.Id, cancel).ConfigureAwait(false) != dependency.Stamp)
                return false;
        }

        return true;
    }

    /// <summary>
    /// What a generator asked for (<paramref name="reads"/>) as the manifest keeps it: each asset with its stamp
    /// now. A generated LOD is left out: the asset it was made of is among them, and its stamp has the generator's
    /// version.
    /// </summary>
    private async Task<LodDependency[]> DependenciesAsync(IEnumerable<(Type Type, ulong Id)> reads, CancellationToken cancel)
    {
        List<LodDependency> dependencies = [];
        foreach ((Type type, ulong id) in reads.OrderBy(read => read.Id))
        {
            if (type.FullName is not { } name || FullPathOf(id) is not { } path || _files.IsUnder(_lodCache, path))
                continue;

            if (await StampAsync(type, id, cancel).ConfigureAwait(false) is { } stamp)
                dependencies.Add(new LodDependency(name, id, stamp));
        }

        return [.. dependencies];
    }

    /// <summary>
    /// Deletes what nothing will ask for again from a type's cache: the asset's folders other than
    /// <paramref name="current"/> (made of an earlier file, or by an earlier generator) and, the first time the
    /// type is asked after, the folders of ids nothing has any more.
    /// </summary>
    private void DeleteStale(Type type, ulong id, string current)
    {
        string root = _files.Combine(_lodCache, type.Name);
        if (_files.ReadDirectory(root, $"{id:x16}-*") is { Ok: true } own)
        {
            foreach (string folder in own.Payload)
            {
                if (!string.Equals(Path.GetFileName(folder), current, StringComparison.OrdinalIgnoreCase))
                    Forget(folder);
            }
        }

        if (!_swept.TryAdd(type, true) || _files.ReadDirectory(root) is not { Ok: true } all)
            return;

        foreach (string folder in all.Payload)
        {
            string name = Path.GetFileName(folder);
            if (name.Length > 16 && ulong.TryParse(name.AsSpan(0, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong owner) && FullPathOf(owner) is null)
                Forget(folder);
        }
    }

    /// <summary>
    /// Takes a folder of generated LODs out of the index and off the disk. Nothing is published: it happens on a
    /// worker, and what was made of the same asset replaces them.
    /// </summary>
    private void Forget(string folder)
    {
        if (!_files.DirectoryExists(folder))
            return;

        lock (_lock)
        {
            foreach (string path in _idByPath.Keys.Where(known => _files.IsUnder(folder, known)).ToArray())
            {
                if (!_idByPath.Remove(path, out ulong id))
                    continue;

                _pathById.Remove(id);
                Evict(id);
            }
        }

        Result deleted = _files.DeleteDirectory(folder);
        if (deleted.Failed)
            Debugging.Log.Warn($"Could not delete the stale LODs in {folder}: {deleted.Message}");
    }

    /// <summary>
    /// The asset alone, from its pool or the file; null at once for one that has failed. Everyone loading one id at
    /// once awaits the same read; a caller that is cancelled stops waiting, and the read stops when the last one is.
    /// </summary>
    private async Task<T?> LoadOneAsync<T>(Handle<T> handle, CancellationToken cancel) where T : Asset
    {
        _generatorReads.Value?.TryAdd((typeof(T), handle.Id), true);
        long budget = BudgetOf(typeof(T));
        if (!TryJoin(handle, budget, out Pool? pool, out Entry? entry))
            return null;

        if (pool is null || entry is null)
            return (T?)(await _threads.InvokeAsync(ThreadId.Worker, cancel => LoadFileAsync(handle, cancel), cancel).Unwrap().ConfigureAwait(false)).Asset;

        try
        {
            (Asset? asset, long bytes) = await entry.Load.WaitAsync(cancel).ConfigureAwait(false);
            Leave(pool, handle.Id, entry, cancelled: false);
            if (asset is null)
                Discard(pool, handle.Id, entry);
            else
                AddToPool(typeof(T), pool, handle.Id, entry, bytes, budget);

            return (T?)asset;
        }
        catch (OperationCanceledException)
        {
            Leave(pool, handle.Id, entry, cancelled: true);
            throw;
        }
    }

    /// <summary>
    /// Finds the asset's entry in its pool, beginning its read when it has none, and counts the caller among those
    /// waiting for it. False for an asset that has failed; no entry for a type that is not pooled.
    /// </summary>
    private bool TryJoin<T>(Handle<T> handle, long budget, out Pool? pool, out Entry? entry) where T : Asset
    {
        pool = null;
        entry = null;
        lock (_lock)
        {
            if (_failed.Contains((typeof(T), handle.Id)))
                return false;

            if (budget == 0)
            {
                Drop(typeof(T), "its budget is 0"); // the budget went to 0 while it held some
                return true;
            }

            if (!_pools.TryGetValue(typeof(T), out pool))
                _pools[typeof(T)] = pool = new Pool();

            bool hit = true;
            if (!pool.Entries.TryGetValue(handle.Id, out entry))
            {
                hit = false;
                CancellationTokenSource cancel = new();
                pool.Entries[handle.Id] = entry = new Entry(_threads.InvokeAsync(ThreadId.Worker, token => LoadFileAsync(handle, token), cancel.Token).Unwrap(), cancel);
            }

            entry.Used = ++_loadCount;
            entry.Waiters++;
            if (hit)
                pool.Hits++;
            else
                pool.Misses++;
        }

        return true;
    }

    /// <summary>
    /// One caller no longer waits for the entry. When it left because it was cancelled and it was the last, the read
    /// is stopped and the entry goes, so the next Load begins it again.
    /// </summary>
    private void Leave(Pool pool, ulong id, Entry entry, bool cancelled)
    {
        lock (_lock)
        {
            entry.Waiters--;
            if (!cancelled || entry.Waiters > 0 || entry.Load.IsCompleted)
                return;

            if (pool.Entries.TryGetValue(id, out Entry? still) && still == entry)
                pool.Remove(id);
        }

        entry.Cancel.Cancel();
    }

    /// <summary>
    /// Loads what <paramref name="holders"/> name and what those name, all at once; each asset once per
    /// <paramref name="seen"/>. Done when all of them are. <paramref name="loaded"/> counts those that are, and
    /// <paramref name="progress"/> is told their share of <paramref name="seen"/> as each arrives.
    /// </summary>
    private Task LoadNamedAsync(IEnumerable<object> holders, ConcurrentDictionary<(Type, ulong), bool> seen, StrongBox<int> loaded, IProgress<float>? progress, CancellationToken cancel)
    {
        List<Task> loads = [];
        foreach (object holder in holders)
        {
            foreach ((Type type, ulong id) in Named(holder, seen))
                loads.Add(FollowAsync(type, id));
        }

        return Task.WhenAll(loads);

        async Task FollowAsync(Type type, ulong id)
        {
            Asset? asset = await LoadOneAsync(type, id, cancel).ConfigureAwait(false);
            if (asset is null)
            {
                progress?.Report((float)Interlocked.Increment(ref loaded.Value) / seen.Count);
                return;
            }

            // Its lesser versions come with it (made here when they are not cached yet), so whoever draws it finds them pooled.
            Asset[] lods = await LoadLodsAsync(type, id, cancel).ConfigureAwait(false);
            Task named = LoadNamedAsync([asset, .. lods], seen, loaded, progress, cancel); // finds what it names before this one counts
            progress?.Report((float)Interlocked.Increment(ref loaded.Value) / seen.Count);
            await named.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The assets <paramref name="holder"/> names that <paramref name="seen"/> has not got yet, now added to it. A
    /// texture handle may name a render texture, which has no image to load: a camera draws it.
    /// </summary>
    private List<(Type Type, ulong Id)> Named(object holder, ConcurrentDictionary<(Type, ulong), bool> seen)
    {
        List<(Type Type, ulong Id)> named = [];
        foreach (object handle in holder.ValuesOf<Handle<Asset>>())
        {
            Type type = handle.GetType().GetGenericArguments()[0];
            if (handle.GetType().GetProperty(nameof(Handle<>.Id))?.GetValue(handle) is ulong id && id != 0)
                named.Add((type, id));
        }

        named.RemoveAll(dependency => !seen.TryAdd(dependency, true) || (dependency.Type == typeof(Texture) && RenderTexture.IsAt(PathOf(dependency.Id))));

        return named;
    }

    /// <summary>
    /// <see cref="LoadOneAsync{T}(Handle{T}, CancellationToken)"/> for a type known only at run time.
    /// </summary>
    private Task<Asset?> LoadOneAsync(Type type, ulong id, CancellationToken cancel)
    {
        return _loads.GetOrAdd(type, static assetType => Method(nameof(LoadBoxedAsync))
            .MakeGenericMethod(assetType)
            .CreateDelegate<Func<Assets, ulong, CancellationToken, Task<Asset?>>>())(this, id, cancel);
    }

    /// <summary>
    /// One of this class's private static methods, by name.
    /// </summary>
    private static MethodInfo Method(string name)
    {
        return typeof(Assets).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static) ?? throw new MissingMethodException(nameof(Assets), name);
    }

    private static async Task<Asset?> LoadBoxedAsync<T>(Assets assets, ulong id, CancellationToken cancel) where T : Asset
    {
        return await assets.LoadOneAsync(new Handle<T>(id), cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// The asset's lesser versions, loaded, for a type known only at run time; those that cannot be loaded are left out.
    /// </summary>
    private Task<Asset[]> LoadLodsAsync(Type type, ulong id, CancellationToken cancel)
    {
        return _lodLoads.GetOrAdd(type, static assetType => Method(nameof(LoadLodsBoxedAsync))
            .MakeGenericMethod(assetType)
            .CreateDelegate<Func<Assets, ulong, CancellationToken, Task<Asset[]>>>())(this, id, cancel);
    }

    private static async Task<Asset[]> LoadLodsBoxedAsync<T>(Assets assets, ulong id, CancellationToken cancel) where T : Asset
    {
        List<Asset> loaded = [];
        foreach ((_, Handle<T> lod) in await assets.LodsAsync(new Handle<T>(id), cancel: cancel).ConfigureAwait(false))
        {
            if (await assets.LoadOneAsync(lod, cancel).ConfigureAwait(false) is { } asset)
                loaded.Add(asset);
        }

        return [.. loaded];
    }

    /// <summary>
    /// The asset read from disk with what it holds in memory, or null (logged, and remembered as failed) and 0 when it
    /// cannot be loaded. Every call reads the file and returns a new object. Runs on a worker.
    /// </summary>
    private async Task<(Asset? Asset, long Bytes)> LoadFileAsync<T>(Handle<T> handle, CancellationToken cancel) where T : Asset
    {
        string? path = FullPathOf(handle.Id);

        try
        {
            if (path is null)
                throw new KeyNotFoundException("no asset has this id.");

            T sidecar = await SidecarAsync<T>(path, cancel).ConfigureAwait(false);
            (T asset, long fileBytes) = await ReadAsync(sidecar, path, cancel).ConfigureAwait(false);
            long bytes = asset.Bytes > 0 ? asset.Bytes : fileBytes;
            Log(typeof(T), $"Loaded {typeof(T).Name} {handle.Id} ({asset.Path}): {bytes / 1024d:0.#} KiB.");

            return (asset, bytes);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            string location = path is null ? "" : $" ({Relative(path)})";
            Debugging.Log.Warn($"Failed to load {typeof(T).Name} {handle.Id}{location}: {ex.Message}");
            lock (_lock)
                _failed.Add((typeof(T), handle.Id));

            return (null, 0);
        }
    }

    /// <summary>
    /// Takes a failed read's entry out of its pool: a pool holds assets, the failure is remembered apart.
    /// </summary>
    private void Discard(Pool pool, ulong id, Entry entry)
    {
        lock (_lock)
        {
            if (pool.Entries.TryGetValue(id, out Entry? still) && still == entry)
                pool.Remove(id);
        }
    }

    /// <summary>
    /// Counts a newly read entry into its pool, once whoever gets there first. When that takes the pool over <paramref name="budget"/>, its least
    /// recently used entries go until it is down to <see cref="TrimmedTo"/> of it, so a full pool evicts once every
    /// few misses and not on each. Never the new one: an asset bigger than its budget stays until the next miss.
    /// </summary>
    private void AddToPool(Type type, Pool pool, ulong id, Entry entry, long bytes, long budget)
    {
        lock (_lock)
        {
            if (entry.Admitted || !pool.Entries.TryGetValue(id, out Entry? still) || still != entry)
                return; // counted already, or its file changed while it was being read: evicted, so not counted

            entry.Admitted = true;
            entry.Bytes = bytes;
            pool.Bytes += bytes;
            if (pool.Bytes <= budget)
                return;

            int count = 0;
            long before = pool.Bytes;
            while (pool.Bytes > budget * TrimmedTo && pool.Oldest(except: id) is { } oldest)
            {
                pool.Remove(oldest);
                count++;
            }

            if (count > 0)
                Log(type, $"Unloaded {count} {type.Name} asset(s), {(before - pool.Bytes) / (double)Megabyte:0.#} MB: their pool is over budget.");
        }
    }

    /// <summary>
    /// A load or an eviction for the log. Chunks come and go all the time while a domain streams, so theirs are
    /// verbose, like the streaming system's own lines.
    /// </summary>
    private static void Log(Type type, string message)
    {
        if (type == typeof(Chunk))
            Debugging.Log.Verbose(message);
        else
            Debugging.Log.Info(message);
    }

    /// <summary>
    /// The type's budget in bytes, from the settings as they are now: by its name, else the default.
    /// </summary>
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

    /// <summary>
    /// Drops a type's whole pool, saying how many assets went and <paramref name="why"/>. Under the lock.
    /// </summary>
    private void Drop(Type type, string why)
    {
        if (_pools.Remove(type, out Pool? pool) && pool.Entries.Count > 0)
            Debugging.Log.Info($"Unloaded {pool.Entries.Count} {type.Name} asset(s): {why}.");
    }

    /// <summary>
    /// An asset for the log: its type and id, with its path while it has one. Under the lock.
    /// </summary>
    private string Describe(Type type, ulong id)
    {
        return _pathById.TryGetValue(id, out string? path) ? $"{type.Name} {id} ({Relative(path)})" : $"{type.Name} {id}";
    }

    /// <summary>
    /// Takes a changed id out of the pools and the failures, so the next Load reads the file. Under the lock.
    /// </summary>
    private void Evict(ulong id)
    {
        foreach ((Type type, Pool pool) in _pools)
        {
            if (pool.Remove(id))
                Debugging.Log.Info($"Unloaded {Describe(type, id)}: its file changed.");
        }

        if (_failed.Count > 0)
            _failed.RemoveWhere(failed => failed.Id == id);

        // Its own LODs, and those a generator made of it for another asset (a chunk's stand-in of its models).
        // Walked, not through Keys: that copies every key into a list, and this runs once per file indexed.
        foreach (KeyValuePair<(Type Type, ulong Id), Lazy<Task<Array>>> found in _lods)
        {
            if (found.Key.Id == id || (_lodDependencies.TryGetValue(found.Key, out ulong[]? dependencies) && dependencies.Contains(id)))
                _lods.TryRemove(found.Key, out _);
        }

        foreach (KeyValuePair<(Type Type, ulong Id), Lazy<Task<string?>>> stamped in _stamps)
        {
            if (stamped.Key.Id == id)
                _stamps.TryRemove(stamped.Key, out _);
        }
    }

    /// <summary>
    /// Gems came or went: the pools of their asset types go (they would keep an unloaded gem's assembly alive) with
    /// whatever else was kept by type, failures are forgotten (a loader may have arrived), and the budget names are checked against the asset types there are now.
    /// </summary>
    private void OnGemsChanged()
    {
        lock (_lock)
        {
            foreach (Type type in _pools.Keys.Where(pooled => pooled.Assembly.IsCollectible).ToArray())
                Drop(type, "gems changed");

            // A script is a Core type, but what it holds is a class of a project assembly that may just have gone.
            Drop(typeof(Script), "gems changed");

            _failed.Clear();
        }

        _loads.Clear();
        _lodLoads.Clear();
        _lods.Clear(); // a generator may have come or gone
        _lodDependencies.Clear();
        _stamps.Clear(); // they carry the generator's version
        _stampsOf.Clear();
        _typesByName.Clear();

        Type[] types = AssetTypes();
        foreach (string name in _project.Settings.Assets.Budgets.Keys)
        {
            if (!types.Any(candidate => IsNamed(candidate, name)))
                Debugging.Log.Warn($"Assets.Budgets: no asset type is called '{name}'.");
        }
    }

    /// <summary>
    /// The sidecar deserialised as the asset type: id, version and the [MetaData] properties, plus the path.
    /// </summary>
    private async Task<T> SidecarAsync<T>(string path, CancellationToken cancel) where T : Asset
    {
        Result<T?> meta = await _files.ReadAsync(path + ".meta", static bytes => JsonSerializer.Deserialize<T>(bytes, AssetJson.Options), cancel).ConfigureAwait(false);
        if (meta.Failed)
            throw new IOException($"could not read its sidecar. {meta.Message}");

        T asset = meta.Payload ?? throw new JsonException("the sidecar is null.");
        asset.Path = Relative(path);

        return asset;
    }

    /// <summary>
    /// Fills the sidecar's asset from the file by its <see cref="AssetFormat"/>: the host reads JSON and text itself; a
    /// custom format, which is also what a type without <see cref="AssetFormatAttribute"/> has, goes to the
    /// <see cref="IAssetLoader{T}"/> a gem provides. With it comes about what the file's contents take in memory:
    /// JSON is read straight from the file's bytes into the asset, so its size on disk stands for that.
    /// </summary>
    private async Task<(T Asset, long FileBytes)> ReadAsync<T>(T asset, string path, CancellationToken cancel) where T : Asset
    {
        AssetFormat format = typeof(T).GetCustomAttribute<AssetFormatAttribute>()?.Format ?? AssetFormat.Custom;
        switch (format)
        {
            case AssetFormat.Json:
            {
                // The file is the same type; the sidecar's values win for what it owns.
                (T? Content, int Bytes) read = ValueOrThrow(await _files.ReadAsync(path, static bytes => (JsonSerializer.Deserialize<T>(bytes, AssetJson.Options), bytes.Length), cancel).ConfigureAwait(false));
                T content = read.Content ?? throw new JsonException("the file is null.");
                content.Id = asset.Id;
                content.Version = asset.Version;
                content.Path = asset.Path;
                foreach (PropertyInfo property in typeof(T).GetProperties().Where(candidate => candidate.IsDefined(typeof(MetaDataAttribute))))
                    property.SetValue(content, property.GetValue(asset));

                return (content, read.Bytes);
            }
            case AssetFormat.Text:
            {
                PropertyInfo property = typeof(T).GetProperty("Text", typeof(string))
                    ?? throw new InvalidOperationException($"{typeof(T).Name} is a text asset but has no string Text property.");
                string text = ValueOrThrow(await _files.ReadTextAsync(path, cancel).ConfigureAwait(false));
                property.SetValue(asset, text);

                return (asset, text.Length * (long)sizeof(char));
            }
            case AssetFormat.Binary:
            {
                PropertyInfo property = typeof(T).GetProperty("Data", typeof(byte[]))
                    ?? throw new InvalidOperationException($"{typeof(T).Name} is a binary asset but has no byte[] Data property.");
                byte[] data = ValueOrThrow(await _files.ReadBinaryAsync(path, cancel).ConfigureAwait(false));
                property.SetValue(asset, data);

                return (asset, data.LongLength);
            }
            case AssetFormat.Custom:
            {
                if (!_container.TryGet(out IAssetLoader<T>? loader))
                    throw new InvalidOperationException($"no loaded gem provides an IAssetLoader<{typeof(T).Name}>.");


                byte[] bytes = ValueOrThrow(await _files.ReadBinaryAsync(path, cancel).ConfigureAwait(false));
                cancel.ThrowIfCancellationRequested(); // an import cannot be stopped once it has begun
                Result loaded = loader.Load(asset, bytes);
                if (loaded.Failed)
                    throw new InvalidDataException(loaded.Message);

                return (asset, bytes.LongLength);
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
            Evict(id); // added, written or moved: a pooled copy (or a remembered failure) is stale
        }

        if (!publish)
            return;

        foreach (Event published in happened)
            _events.Publish(published);
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

        try
        {
            Result<ulong> read = _files.Read(metaPath, IdOf);
            if (read.Failed)
                Debugging.Log.Warn($"Could not read {metaPath}: {read.Message}");

            return read.Failed ? 0 : read.Payload;
        }
        catch (JsonException ex)
        {
            Debugging.Log.Warn($"{metaPath} is not valid JSON: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// The <c>id</c> at the top of a sidecar's JSON, or 0 when it has none; nothing else of the file is made.
    /// </summary>
    private static ulong IdOf(ReadOnlySpan<byte> json)
    {
        Utf8JsonReader reader = new(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            return 0;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            bool id = reader.ValueTextEquals("id"u8);
            reader.Read();
            if (id)
                return reader.TokenType == JsonTokenType.Number && reader.TryGetUInt64(out ulong value) ? value : 0;

            reader.Skip();
        }

        return 0;
    }

    /// <summary>
    /// Gives a file without a sidecar a fresh id, so it can be referenced from now on.
    /// </summary>
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

    /// <summary>
    /// The path relative to the folder it is under, forward slashes.
    /// </summary>
    private string Relative(string path)
    {
        foreach ((string root, _) in _folders)
        {
            if (_files.IsUnder(root, path))
                return string.Join('/', _files.Segments(_files.Relative(root, path)));
        }

        return string.Join('/', _files.Segments(path));
    }

    /// <summary>
    /// What <see cref="Mint"/> writes for <paramref name="id"/>.
    /// </summary>
    private static string SidecarText(ulong id)
    {
        return $"{{\n    \"id\": {id},\n    \"version\": 1\n}}\n";
    }

    private static TPayload ValueOrThrow<TPayload>(Result<TPayload> read)
    {
        return read.Failed ? throw new IOException(read.Message) : read.Payload;
    }

    private static bool IsSidecar(string path)
    {
        return path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a budget name is the type's: its name or full name, any case.
    /// </summary>
    private static bool IsNamed(Type type, string name)
    {
        return string.Equals(type.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(type.FullName, name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every concrete asset type in Core and the assemblies that reference it (the gems).
    /// </summary>
    private static Type[] AssetTypes()
    {
        Assembly core = typeof(Asset).Assembly;
        string? coreName = core.GetName().Name;
        List<Type> found = [];
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
                types = [.. ex.Types.OfType<Type>()];
            }

            found.AddRange(types.Where(candidate => !candidate.IsAbstract && typeof(Asset).IsAssignableFrom(candidate)));
        }

        return [.. found];
    }

    /// <summary>
    /// One asset type's loaded assets by id, with what they take and how often a Load found one. Used under the lock.
    /// </summary>
    private sealed class Pool
    {
        public Dictionary<ulong, Entry> Entries { get; } = [];

        /// <summary>
        /// What the counted entries hold; an entry still being read counts nothing yet.
        /// </summary>
        public long Bytes { get; set; }

        public long Hits { get; set; }

        public long Misses { get; set; }

        /// <summary>
        /// The least recently loaded id other than <paramref name="except"/>; null when there is none.
        /// </summary>
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

        /// <summary>
        /// Whether <paramref name="id"/> was there to take out.
        /// </summary>
        public bool Remove(ulong id)
        {
            if (!Entries.Remove(id, out Entry? entry))
                return false;

            Bytes -= entry.Bytes;

            return true;
        }
    }

    /// <summary>
    /// What a folder of generated LODs holds, as its <c>lods.json</c> says: each threshold's file, and every other
    /// asset the generator loaded to make them.
    /// </summary>
    private sealed record LodManifest(Dictionary<float, string> Lods, LodDependency[] Dependencies);

    /// <summary>
    /// Another asset some generated LODs were made from: its type's full name, its id and its stamp at the time.
    /// </summary>
    private sealed record LodDependency(string Type, ulong Id, string Stamp);

    /// <summary>
    /// One pooled asset: read once, for everyone who loads it while it is there.
    /// </summary>
    private sealed class Entry(Task<(Asset? Asset, long Bytes)> load, CancellationTokenSource cancel)
    {
        public Task<(Asset? Asset, long Bytes)> Load { get; } = load;

        /// <summary>
        /// Stops the read; used when the last caller waiting for it is cancelled.
        /// </summary>
        public CancellationTokenSource Cancel { get; } = cancel;

        /// <summary>
        /// The callers awaiting <see cref="Load"/> now.
        /// </summary>
        public int Waiters { get; set; }

        /// <summary>
        /// Whether it has been counted into its pool.
        /// </summary>
        public bool Admitted { get; set; }

        /// <summary>
        /// What it counts for in its pool; 0 until it is admitted.
        /// </summary>
        public long Bytes { get; set; }

        /// <summary>
        /// When it was last loaded, by the manager's load count.
        /// </summary>
        public long Used { get; set; }
    }
}
