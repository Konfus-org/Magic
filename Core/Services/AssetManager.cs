using Magic.Attributes;
using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Utils;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Magic.Services;

/// <summary>
/// Knows where every asset is and loads one on request; keeps none. Every file under a watched folder
/// (Resources, Assets, and whatever <see cref="AddFolder"/> adds) is an asset, and its <c>.meta</c> sidecar
/// (written when missing) holds the id a <see cref="Handle{T}"/> carries. The folders are watched:
/// <see cref="ProcessChanges"/>, called by the main loop, applies settled changes to the registry and
/// publishes <see cref="AssetAdded"/>, <see cref="AssetModified"/>, <see cref="AssetMoved"/> and
/// <see cref="AssetRemoved"/> on the <see cref="EventBus"/>. Whoever holds an asset decides what to do then.
/// </summary>
public sealed class AssetManager : IDisposable
{
    /// <summary>How long a file must be quiet before its change is applied; a save touches a file several times.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

    private readonly IFileSystem _files;
    private readonly EventBus _events;
    private readonly List<(string Root, IDisposable Watcher)> _folders = [];
    private readonly Dictionary<ulong, string> _pathById = [];
    private readonly Dictionary<string, ulong> _idByPath = new(StringComparer.OrdinalIgnoreCase); // full paths
    private readonly Dictionary<string, long> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _ownWrites = new(StringComparer.OrdinalIgnoreCase); // sidecar path -> what was written
    private readonly Lock _lock = new();

    public AssetManager(Project project, IFileSystem files, EventBus events)
    {
        _files = files;
        _events = events;
        if (files.DirectoryExists(project.Resources))
            AddFolder(project.Resources);
        if (files.DirectoryExists(project.Assets))
            AddFolder(project.Assets);
    }

    public void Dispose()
    {
        foreach ((_, IDisposable watcher) in _folders)
            watcher.Dispose();
        _folders.Clear();
    }

    /// <summary>Indexes and watches another folder; every file under it is an asset. Main thread.</summary>
    public void AddFolder(string folder)
    {
        string root = _files.FullPath(folder);
        if (!_files.DirectoryExists(root))
            throw new DirectoryNotFoundException(root);
        if (_folders.Any(f => string.Equals(f.Root, root, StringComparison.OrdinalIgnoreCase)))
            return;

        _folders.Add((root, _files.Watch(root, null, Notify, Notify, recursive: true)));
        Result<string[]> listing = _files.ReadDirectoryRecursive(root);
        if (listing.Failed)
        {
            Debugging.LogWarning($"Could not index assets in {root}: {listing.Message}");
            return;
        }
        foreach (string path in listing.Payload)
        {
            if (_files.FileExists(path) && !IsSidecar(path))
                Register(path, publish: false);
        }
    }

    /// <summary>
    /// The handle of the asset at <paramref name="path"/>, relative to any watched folder (forward slashes,
    /// like <see cref="Asset.Path"/>); <see cref="Handle{T}.None"/> when no such file is indexed. For code that
    /// starts from a name; files reference each other by id.
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

    /// <summary>The relative path of an asset, or null for an id nothing has.</summary>
    public string? PathOf(ulong id)
    {
        lock (_lock)
        {
            return _pathById.TryGetValue(id, out string? path) ? Relative(path) : null;
        }
    }

    /// <summary>
    /// A fresh copy of the asset, loaded from disk on the calling thread; null when it cannot be loaded
    /// (logged). The manager keeps nothing: the caller owns what comes back and reloads on <see cref="AssetModified"/>.
    /// </summary>
    public T? Load<T>(Handle<T> handle) where T : Asset
    {
        string? path = PathFor(handle.Id);
        try
        {
            T asset = Sidecar<T>(path);
            Result<byte[]> read = _files.ReadBinary(path);
            if (read.Failed)
                throw new IOException(read.Message);

            if (BuiltIn(asset, read.Payload) is { } filled)
                return filled;
            Loader<T>().Load(asset, read.Payload);
            return asset;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debugging.LogWarning(Failure<T>(handle.Id, path, ex));
            return null;
        }
    }

    /// <summary>
    /// The same as <see cref="Load{T}"/>, reading and decoding off the calling thread with progress 0..1 and cancellation.
    /// </summary>
    public async Task<T?> LoadAsync<T>(Handle<T> handle, IProgress<double>? progress = null, CancellationToken cancellationToken = default) where T : Asset
    {
        string? path = PathFor(handle.Id);
        try
        {
            T asset = Sidecar<T>(path);
            Result<byte[]> read = await _files.ReadBinaryAsync(path, progress.Scale(0, 0.5), cancellationToken).ConfigureAwait(false);
            if (read.Failed)
                throw new IOException(read.Message);

            if (BuiltIn(asset, read.Payload) is { } filled)
                asset = filled;
            else
                await Loader<T>().LoadAsync(asset, read.Payload, progress.Scale(0.5, 1), cancellationToken).ConfigureAwait(false);

            progress?.Report(1);
            return asset;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debugging.LogWarning(Failure<T>(handle.Id, path, ex));
            return null;
        }
    }

    private string? PathFor(ulong id)
    {
        lock (_lock)
        {
            return _pathById.GetValueOrDefault(id);
        }
    }

    /// <summary>The sidecar deserialised as the asset type: id, version and the [MetaData] properties, plus the path.</summary>
    private T Sidecar<T>(string? path) where T : Asset
    {
        if (path is null)
            throw new KeyNotFoundException("no asset has this id.");

        Result<byte[]> meta = _files.ReadBinary(path + ".meta");
        if (meta.Failed)
            throw new IOException($"could not read its sidecar. {meta.Message}");

        T asset = JsonSerializer.Deserialize<T>(StripBom(meta.Payload).Span, AssetJson.Options)
            ?? throw new JsonException("the sidecar is null.");

        asset.Path = Relative(path);

        return asset;
    }

    private string Failure<T>(ulong id, string? path, Exception ex)
    {
        return $"Failed to load {typeof(T).Name} {id}{(path is null ? "" : $" ({Relative(path)})")}: {ex.Message}";
    }

    /// <summary>
    /// Applies every queued file change that has been quiet for <see cref="Settle"/> and publishes what
    /// happened. Called by the main loop at the start of each frame, so the registry and the events only
    /// change between frames.
    /// </summary>
    public void ProcessChanges()
    {
        List<string> ready;
        lock (_lock)
        {
            ready = [.. _pending.Where(p => Stopwatch.GetElapsedTime(p.Value) >= Settle).Select(p => p.Key)];
            foreach (string path in ready)
                _pending.Remove(path);
        }
        if (ready.Count == 0)
            return;

        // A sidecar change is its asset's change; a folder change is every file's under it (a moved folder
        // reports only itself). Files that exist go first, so a move claims its id before the old path is
        // seen as gone.
        HashSet<string> present = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> missing = new(StringComparer.OrdinalIgnoreCase);
        foreach (string notified in ready)
        {
            string path = IsSidecar(notified) ? notified[..^".meta".Length] : notified;
            if (_files.DirectoryExists(path))
            {
                Result<string[]> listing = _files.ReadDirectoryRecursive(path);
                if (listing.Ok)
                    present.UnionWith(listing.Payload.Where(p => _files.FileExists(p) && !IsSidecar(p)));
            }
            else if (_files.FileExists(path))
                present.Add(path);
            else
            {
                missing.Add(path);
                lock (_lock)
                {
                    missing.UnionWith(_idByPath.Keys.Where(p => p.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
                }
            }
        }

        foreach (string path in present)
        {
            try
            {
                Register(path, publish: true);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Debugging.LogWarning($"Handling a change to {path} failed. {ex}");
            }
        }
        foreach (string path in missing)
        {
            ulong id;
            lock (_lock)
            {
                if (!_idByPath.Remove(path, out id))
                    continue;
                _pathById.Remove(id);
            }
            _events.Publish(new AssetRemoved(id, Relative(path)));
        }
    }

    /// <summary>
    /// Puts the file at <paramref name="path"/> in the registry under its sidecar's id and, when asked, says
    /// what changed: added, modified, or moved here from a path that no longer exists.
    /// </summary>
    private void Register(string path, bool publish)
    {
        ulong id = ReadId(path + ".meta") ?? Mint(path + ".meta");
        if (id == 0)
            return;

        string relative = Relative(path);
        AssetMoved? moved = null;
        AssetRemoved? replaced = null;
        bool added = false;
        lock (_lock)
        {
            if (_idByPath.TryGetValue(path, out ulong previous) && previous != id)
            {
                // The sidecar was rewritten with another id: the old one is gone from this path.
                _idByPath.Remove(path);
                _pathById.Remove(previous);
                replaced = new AssetRemoved(previous, relative);
            }

            if (_pathById.TryGetValue(id, out string? elsewhere) && !string.Equals(elsewhere, path, StringComparison.OrdinalIgnoreCase))
            {
                if (_files.FileExists(elsewhere))
                {
                    Debugging.LogError($"{relative} and {Relative(elsewhere)} share id {id}; keeping {Relative(elsewhere)}.");
                    return;
                }
                _idByPath.Remove(elsewhere);
                moved = new AssetMoved(id, Relative(elsewhere), relative);
            }
            else if (elsewhere is null)
                added = true;

            _pathById[id] = path;
            _idByPath[path] = id;
        }

        if (!publish)
            return;
        if (replaced is { } r)
            _events.Publish(in r);
        if (moved is { } m)
            _events.Publish(in m);
        else if (added)
            _events.Publish(new AssetAdded(id, relative));
        else
            _events.Publish(new AssetModified(id, relative));
    }

    /// <summary>Newest change per path wins. Safe from any thread (the watcher's).</summary>
    private void Notify(string path)
    {
        lock (_lock)
        {
            // A sidecar this manager wrote is not a change anyone needs to hear about, as long as it still
            // holds what was written; a write of something else is.
            if (_ownWrites.TryGetValue(path, out string? written))
            {
                Result<byte[]> now = _files.ReadBinary(path);
                if (now.Ok && Encoding.UTF8.GetString(now.Payload) == written)
                    return;
                _ownWrites.Remove(path);
            }
            _pending[path] = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>
    /// Fills <paramref name="asset"/> from the file's content for an <see cref="AssetFormatAttribute"/> type (JSON
    /// or text) and returns the result; null for a type that needs a gem loader.
    /// </summary>
    private static T? BuiltIn<T>(T asset, byte[] bytes) where T : Asset
    {
        switch (typeof(T).GetCustomAttribute<AssetFormatAttribute>()?.Format)
        {
            case AssetFormat.Json:
            {
                // The file is the same type; the sidecar's values win for what it owns.
                T content = JsonSerializer.Deserialize<T>(StripBom(bytes).Span, AssetJson.Options)
                    ?? throw new JsonException("the file is null.");
                content.Id = asset.Id;
                content.Version = asset.Version;
                content.Path = asset.Path;
                foreach (PropertyInfo property in typeof(T).GetProperties().Where(p => p.IsDefined(typeof(MetaDataAttribute))))
                    property.SetValue(content, property.GetValue(asset));
                return content;
            }
            case AssetFormat.Text:
            {
                PropertyInfo text = typeof(T).GetProperty("Text", typeof(string))
                    ?? throw new InvalidOperationException($"{typeof(T).Name} is a text asset but has no string Text property.");
                text.SetValue(asset, Encoding.UTF8.GetString(StripBom(bytes).Span));
                return asset;
            }
            default:
                return null;
        }
    }

    private static IAssetLoader<T> Loader<T>() where T : Asset
    {
        return AssetLoaderRegistry.Get<T>()
            ?? throw new InvalidOperationException($"no loaded gem provides an IAssetLoader<{typeof(T).Name}>.");
    }

    private ulong? ReadId(string metaPath)
    {
        if (!_files.FileExists(metaPath))
            return null;
        Result<byte[]> read = _files.ReadBinary(metaPath);
        if (read.Failed)
        {
            Debugging.LogWarning($"Could not read {metaPath}: {read.Message}");
            return 0;
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                StripBom(read.Payload),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return document.RootElement.TryGetProperty("id", out JsonElement id) && id.TryGetUInt64(out ulong value) ? value : 0;
        }
        catch (JsonException ex)
        {
            Debugging.LogWarning($"{metaPath} is not valid JSON: {ex.Message}");
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

        string text = $"{{\n    \"id\": {id},\n    \"version\": 1\n}}\n";
        lock (_lock)
        {
            _ownWrites[metaPath] = text;
        }

        Result write = _files.WriteText(metaPath, text);
        if (write.IsSuccess)
            return id;

        Debugging.LogWarning($"Could not write {metaPath}: {write.Message}");

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

    private static bool IsSidecar(string path)
    {
        return path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase);
    }

    private static ReadOnlyMemory<byte> StripBom(byte[] bytes)
    {
        return bytes.AsMemory(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0);
    }
}
