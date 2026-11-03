using System.Diagnostics;
using Magic.Assets;
using Magic.Caching;
using Magic.Debugging;
using Magic.Files;
using Magic.Pipelines;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.Gems;

/// <summary>
/// The code's side of a run: watches the folders of the gems it found, and restarts the run once a change there has
/// settled, so the next run loads what was built, with the world where this one left it.
/// </summary>
internal sealed class CodePipeline(App app, World world, Caches caches, IFileSystem files)
    : IPipeline<GemsFound>,
        IPipeline<ServicePump>,
        IPipeline<RunEnding>,
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

    private bool _isRestarting;

    /// <summary>
    /// Whether the world had finished arriving when the restart was asked for. Read then, as the run's end cancels what is
    /// still loading.
    /// </summary>
    private bool _isWorldWhole;

    /// <summary>
    /// The saved world the next run puts back; none when it opens the entry point. Static as it outlives a run, and only in
    /// memory, so a file left by a crash is never put back.
    /// </summary>
    internal static Handle<WorldState> Kept { get; private set; }

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
        _isRestarting = true;
        _isWorldWhole = !world.IsLoading && !world.Contents.IsEmpty;
        app.Restart();

        return ValueTask.FromResult(state);
    }

    /// <summary>
    /// A world still arriving isn't saved: the next run puts back what was kept before it, or opens the entry point.
    /// </summary>
    public async ValueTask<RunEnding> RunAsync(RunEnding state, CancellationToken cancel)
    {
        if (!_isRestarting)
            Kept = Handle<WorldState>.None;
        else if (_isWorldWhole)
            Kept = await KeepWorldAsync(cancel).ConfigureAwait(false);

        return state;
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

    /// <summary>
    /// The world written down where the next run's assets find it; none, which is logged, when it can't be.
    /// </summary>
    private async Task<Handle<WorldState>> KeepWorldAsync(CancellationToken cancel)
    {
        if (world.Writer is not { } writer)
        {
            Log.Warn("The world goes back to the entry point: no gem the project lists writes saved worlds.");

            return Handle<WorldState>.None;
        }

        WorldState saved = await world.SaveAsync(cancel).ConfigureAwait(false);
        string path = files.Combine(caches.KeptFolder, "Reload.world");
        Result<Handle<WorldState>> written = await writer.WriteAsync(saved, path, null, cancel).ConfigureAwait(false);
        if (!written.Ok)
        {
            Log.Error($"The world goes back to the entry point: {path} could not be written: {written.Error}");

            return Handle<WorldState>.None;
        }

        Log.Info($"Kept the world: {saved.Entities.Length} entities in {saved.Contents.Length} contents.");

        return written.Value;
    }
}
