using DryIoc;
using Magic.Contexts;
using Magic.Interfaces;
using Magic.Utils;

namespace Magic.Services;

/// <summary>
/// Owns the set of loaded gems and the order they were loaded in. Gems exporting <see cref="ILogger"/> go
/// first (and so are unloaded last); everything else loads after whatever exports the services its
/// constructors need. Unloading or reloading a gem takes every gem depending on it down first.
/// </summary>
internal sealed class GemManager
{
    private readonly GemLoader _loader;
    private readonly IContainer _container;
    private readonly IFileOperations _fileOperations;
    private readonly List<GemContext> _loaded = [];
    private readonly Dictionary<string, GemContext> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GemManager(GemLoader loader, IContainer container, IFileOperations fileOperations)
    {
        _loader = loader;
        _container = container;
        _fileOperations = fileOperations;
    }

    /// <summary>Loaded gems in load order (a snapshot).</summary>
    public IReadOnlyList<GemContext> Loaded
    {
        get
        {
            _gate.Wait();
            try
            {
                return _loaded.ToArray();
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    public async Task<IReadOnlyList<GemContext>> LoadAllAsync(string directory, CancellationToken cancellationToken = default)
    {
        Result<string[]> files = await _fileOperations
            .ReadDirectoryAsync(directory, "*.dll", null, cancellationToken)
            .ConfigureAwait(false);
        if (files.Failed)
        {
            Log.Warn($"Could not list gems in {directory}: {files.Message}");
            return [];
        }
        return await LoadAsync(files.Payload).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GemContext>> LoadAsync(params string[] paths)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return Load(paths, []);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UnloadAsync(string path)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_byPath.TryGetValue(path, out GemContext? target))
                Unload(target, captureState: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Hot reload: captures reload state from the gem and everything depending on it, unloads them all
    /// (dependents first), then loads them again in dependency order and hands the state back.
    /// </summary>
    public async Task ReloadAsync(string path)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_byPath.TryGetValue(path, out GemContext? target))
            {
                Load([path], []);
                return;
            }

            (string[] paths, Dictionary<string, byte[]> state) = Unload(target, captureState: true);
            Load(paths, state);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RenameAsync(string oldPath, string newPath)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_byPath.Remove(oldPath, out GemContext? gem))
            {
                gem.Path = newPath;
                _byPath[newPath] = gem;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Unloads every gem in reverse load order, except that logger gems always go last (each one is flushed
    /// before it is unregistered), so everything the other gems log on the way down is still written.
    /// </summary>
    public void UnloadAll()
    {
        _gate.Wait();
        try
        {
            foreach (GemContext gem in _loaded.Where(g => !g.IsLoggerGem).Reverse().ToList())
                Remove(gem);
            foreach (GemContext gem in _loaded.AsEnumerable().Reverse().ToList())
                Remove(gem);
        }
        finally
        {
            _gate.Release();
        }
    }

    private IReadOnlyList<GemContext> Load(IReadOnlyList<string> paths, Dictionary<string, byte[]> state)
    {
        List<GemManifest> manifests = [];
        foreach (string path in paths)
        {
            if (_byPath.ContainsKey(path))
            {
                Log.Warn($"Gem {path} is already loaded.");
                continue;
            }
            if (!GemLoader.ReferencesHost(path))
                continue;
            GemManifest? manifest = _loader.TryInspect(path);
            if (manifest is not null)
                manifests.Add(manifest);
        }

        List<GemManifest> order = GemOrdering.TopoSort(
            manifests,
            provides: m => m.Provides,
            requires: m => m.Requires,
            hostProvides: t => _container.IsRegistered(t),
            priority: m => (m.Provides.Contains(typeof(ILogger)) ? 0 : 1, m.Meta.Name),
            skip: (m, why) =>
            {
                Log.Warn($"Skipping gem {m.Meta.Name} ({m.Path}): {why}.");
                m.Alc.Unload();
            });
        manifests.Clear();

        List<GemContext> loaded = [];
        foreach (GemManifest manifest in order)
        {
            GemContext? gem = _loader.Construct(manifest, state.GetValueOrDefault(manifest.Path));
            if (gem is null)
                continue;
            _loaded.Add(gem);
            _byPath[gem.Path] = gem;
            loaded.Add(gem);
            Log.Info($"Loaded gem: {gem.Name} v{gem.Version}{(gem.Author is null ? "" : $" by {gem.Author}")}");
        }
        return loaded;
    }

    /// <summary>
    /// Unloads <paramref name="target"/> and every gem depending on it, dependents first.
    /// Returns the paths in their original load order, plus any captured reload state.
    /// </summary>
    private (string[] Paths, Dictionary<string, byte[]> State) Unload(GemContext target, bool captureState)
    {
        HashSet<GemContext> group = [target];
        Queue<GemContext> pending = new([target]);
        while (pending.TryDequeue(out GemContext? current))
        {
            foreach (GemContext other in _loaded)
            {
                if (other.Requires.Overlaps(current.Provides) && group.Add(other))
                    pending.Enqueue(other);
            }
        }

        List<GemContext> ordered = _loaded.Where(group.Contains).ToList();
        Dictionary<string, byte[]> state = [];
        for (int i = ordered.Count - 1; i >= 0; i--)
        {
            GemContext gem = ordered[i];
            if (captureState && _loader.CaptureState(gem) is { } bytes)
                state[gem.Path] = bytes;
            Remove(gem);
        }
        return (ordered.Select(g => g.Path).ToArray(), state);
    }

    private void Remove(GemContext gem)
    {
        Log.Info($"Unloading gem: {gem.Name}");
        _loader.Unload(gem);
        _loaded.Remove(gem);
        _byPath.Remove(gem.Path);
    }
}
