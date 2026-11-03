using System.Diagnostics;
using Magic.Debugging;
using Magic.Files;
using Magic.Pipelines;
using Magic.Utils;

namespace Magic.Gems;

/// <summary>
/// The code's side of a run: watches the folders of the gems it found, and restarts the run once a change there has
/// settled, so the next run loads what was built.
/// </summary>
internal sealed class CodePipeline(App app, IFileSystem files)
    : IPipeline<GemsFound>,
        IPipeline<ServicePump>,
        IPipeline<AppEnd>
{
    /// <summary>
    /// How long a gem's files must have been left alone: a build writes each gem and its dependencies in turn.
    /// </summary>
    private static readonly TimeSpan _settleTime = TimeSpan.FromSeconds(1);

    private readonly Subscriptions _watches = [];

    /// <summary>
    /// When a watch last saw a change, 0 when none has: written whole from the watches' threads.
    /// </summary>
    private long _touchedTimestamp;

    public ValueTask<GemsFound> RunAsync(GemsFound state, CancellationToken cancel)
    {
        foreach (GemInfo gem in state.Registry.Gems)
        {
            Watch(gem);
        }

        return ValueTask.FromResult(state);
    }

    public ValueTask<ServicePump> RunAsync(ServicePump state, CancellationToken cancel)
    {
        long touched = Volatile.Read(ref _touchedTimestamp);
        if (touched == 0 || Stopwatch.GetElapsedTime(touched) < _settleTime)
            return ValueTask.FromResult(state);

        Volatile.Write(ref _touchedTimestamp, 0);
        Log.Info("A gem's code changed, so the run restarts.");
        app.Restart();

        return ValueTask.FromResult(state);
    }

    public ValueTask<AppEnd> RunAsync(AppEnd state, CancellationToken cancel)
    {
        _watches.Dispose();

        return ValueTask.FromResult(state);
    }

    private void Watch(GemInfo gem)
    {
        Result<IFileWatcher> watched = files.Watch(gem.Folder, "*.dll");
        if (!watched.Ok)
        {
            Log.Warn($"Gem {gem.Name} is not watched, so a build of it won't restart the run: {watched.Error}");

            return;
        }

        IFileWatcher watcher = watched.Value;
        watcher.Created += (_, _) => Touch();
        watcher.Changed += (_, _) => Touch();
        watcher.Deleted += (_, _) => Touch();
        watcher.Renamed += (_, _) => Touch();
        _watches.Add(new Subscription(watcher.Dispose));
    }

    private void Touch() => Volatile.Write(ref _touchedTimestamp, Stopwatch.GetTimestamp());
}
