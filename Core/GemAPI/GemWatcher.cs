namespace Core.GemAPI;

internal sealed class GemWatcher
{
    private readonly FileSystemWatcher _watcher;

    public GemWatcher(string path, GemRegistry registry, GemLoader loader)
    {
        _watcher = new FileSystemWatcher(path);
        _watcher.Created += async (sender, e) =>
        {
            LoadedGem? gem = await loader
                .LoadSingleAsync(e.FullPath, null, CancellationToken.None)
                .ConfigureAwait(false);
            if (gem is not null)
            {
                registry.Register(e.FullPath, gem);
            }
        };
        _watcher.Renamed += (sender, e) =>
        {
            if (registry.TryGetValue(e.OldFullPath, out LoadedGem? gem))
            {
                registry.Unregister(e.OldFullPath);
                registry.Register(e.FullPath, gem);
            }
        };
        _watcher.Deleted += (sender, e) =>
        {
            if (registry.TryGetValue(e.FullPath, out LoadedGem? gem))
            {
                gem.Instance.OnUnload();
                loader.Unload(gem);
                registry.Unregister(e.FullPath);
            }
        };
        _watcher.Changed += async (sender, e) =>
        {
            switch (e.ChangeType)
            {
                case WatcherChangeTypes.Changed:
                {
                    if (registry.TryGetValue(e.FullPath, out LoadedGem? gem))
                    {
                        // Unload the gem, save out any state, and remove it from the registry
                        byte[] persistData = Array.Empty<byte>();
                        gem.Instance.OnReloading(persistData);
                        loader.Unload(gem);
                        registry.Unregister(e.FullPath);

                        // Load the gem again and restore its state
                        LoadedGem? reloadedGem = await loader
                            .LoadSingleAsync(e.FullPath, null, CancellationToken.None)
                            .ConfigureAwait(false);
                        if (reloadedGem is not null)
                        {
                            reloadedGem.Instance.OnReloaded(persistData);
                            registry.Register(e.FullPath, reloadedGem);
                        }
                    }
                    break;
                }
                // Already handled above....
                case WatcherChangeTypes.Deleted:
                case WatcherChangeTypes.Renamed:
                case WatcherChangeTypes.Created:
                default:
                    break;
            }
        };
    }
}
