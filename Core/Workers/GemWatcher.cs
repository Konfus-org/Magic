using Magic.Services;
using Magic.Utils;

namespace Magic.Workers;

/// <summary>
/// Watches the gems folder and loads, unloads, renames or hot reloads gems as their dlls change.
/// Events are debounced per file: a build touches a dll several times while it is still locked.
/// </summary>
internal sealed class GemWatcher : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    private readonly FileSystemWatcher _watcher;
    private readonly GemManager _manager;
    private readonly Dictionary<string, CancellationTokenSource> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public GemWatcher(string path, GemManager manager)
    {
        _manager = manager;
        _watcher = new FileSystemWatcher(path)
        {
            Filter = "*.dll",
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _watcher.Created += (_, e) => Schedule(e.FullPath, () => _manager.ReloadAsync(e.FullPath));
        _watcher.Changed += (_, e) => Schedule(e.FullPath, () => _manager.ReloadAsync(e.FullPath));
        _watcher.Deleted += (_, e) => Schedule(e.FullPath, () => _manager.UnloadAsync(e.FullPath));
        _watcher.Renamed += (_, e) => Schedule(e.FullPath, () => _manager.RenameAsync(e.OldFullPath, e.FullPath));
        _watcher.EnableRaisingEvents = true;
    }

    private void Schedule(string path, Func<Task> action)
    {
        CancellationTokenSource cts = new();
        lock (_lock)
        {
            if (_pending.Remove(path, out CancellationTokenSource? previous))
            {
                previous.Cancel();
                previous.Dispose();
            }
            _pending[path] = cts;
        }

        _ = RunAsync(path, action, cts);
    }

    private async Task RunAsync(string path, Func<Task> action, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(Debounce, cts.Token).ConfigureAwait(false);
            lock (_lock)
            {
                if (_pending.TryGetValue(path, out CancellationTokenSource? current) && current == cts)
                    _pending.Remove(path);
            }
            await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer event for the same file.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Handling a change to {path} failed. {ex}");
        }
    }

    public void Dispose()
    {
        _watcher.Dispose();
        lock (_lock)
        {
            foreach (CancellationTokenSource cts in _pending.Values)
            {
                cts.Cancel();
                cts.Dispose();
            }
            _pending.Clear();
        }
    }
}
