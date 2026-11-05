using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Events;
using Magic.Files;
using Magic.Platform;
using Magic.Utils;
using Magic.Worlds;
using Microsoft.Extensions.DependencyInjection;

namespace Magic.Projects;

/// <summary>
/// The run's gems: makes the ones the registry found as it is made, each after the gems it needs, drives them through the
/// frame, restarts the run once a build of one has settled, and ends them as it is disposed. A gem that can't be made is
/// logged, and the others go on without it; one that throws in a hook is logged and leaves every hook, and the frame goes
/// on.
/// </summary>
internal sealed class Gems : IDisposable
{
    /// <summary>
    /// How long a gem's files must have been left alone after a build wrote them.
    /// </summary>
    private static readonly TimeSpan _settleTime = TimeSpan.FromSeconds(1);

    private readonly GemRegistry _registry;
    private readonly IServiceProvider _provider;
    private readonly App _app;
    private readonly IFileSystem _files;
    private readonly Assets _assets;
    private readonly Assemblies _assemblies;
    private readonly World _world;
    private readonly EventCoordinator _events;
    private readonly FrameClock _clock = new();
    private readonly List<LoadedGem> _loaded = [];
    private readonly Subscriptions _watches = [];

    /// <summary>
    /// When a watch last saw a build write a gem's files, 0 when none has: written whole from the watches' threads.
    /// </summary>
    private long _buildTimestamp;

    /// <summary>
    /// Makes the gems through <paramref name="provider"/>, which was built from the services they were registered with.
    /// </summary>
    public Gems(
        GemRegistry registry,
        IServiceProvider provider,
        App app,
        IFileSystem files,
        Assets assets,
        Assemblies assemblies,
        World world,
        EventCoordinator events
    )
    {
        _registry = registry;
        _provider = provider;
        _app = app;
        _files = files;
        _assets = assets;
        _assemblies = assemblies;
        _world = world;
        _events = events;
        Load();
    }

    /// <summary>
    /// Ends the gems, last made first, so no gem outlives one it was made after.
    /// </summary>
    public void Dispose()
    {
        _watches.Dispose();
        for (int i = _loaded.Count - 1; i >= 0; i--)
        {
            Log.Info($"Unloading {_loaded[i].Name}.");
            _loaded[i].Dispose();
        }

        _loaded.Clear();
    }

    /// <summary>
    /// One frame: restarts the run if a gem was built, works out the clocks, runs every gem's hooks in order with the fixed
    /// steps the clock owes, and sleeps until the next frame is due. Time begins with the first frame, not with what was
    /// loaded before it.
    /// </summary>
    public void Pump()
    {
        RestartIfBuilt();

        // While the world loads, its time runs as it is, as the gems may need steps to get ready.
        bool isPaused = _app.IsPaused || (_world.IsPaused && !_world.IsLoading);
        float timeScale = _world.IsLoading ? 1 : _world.TimeScale;
        Frame frame = _clock.Advance(isPaused, timeScale, out int fixedSteps) with { Events = _events.Take() };

        RunHook(Hook.Update, frame);
        for (int step = 1; step <= fixedSteps; step++)
        {
            RunHook(Hook.FixedUpdate, _clock.GetFixedStep(frame, step));
        }

        _clock.EndFixedSteps(fixedSteps);
        RunHook(Hook.LateUpdate, frame);
        RunHook(Hook.Render, frame);
        _clock.SleepUntilDue();
    }

    /// <summary>
    /// Makes each gem, after registering what it brings so its constructor can find it: its folder of assets, its asset
    /// types and its code, which goes when it does. Then it sets the gem up: the app's window for as long as it lasts when it provides one, the world's ECS, its
    /// place on the log when it is a sink, and the asset types it reads and writes.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever a gem's constructor throws, it is logged and the gem is not made; the others go on."
    )]
    private void Load()
    {
        foreach (GemInfo gem in GetSorted())
        {
            Watch(gem);
            if (gem.Resources is not null)
                _assets.Register(gem.Resources);

            _assets.Register(gem.Type.Assembly);
            Subscription code = _assemblies.Register(gem.Type.Assembly);
            IGem made;
            try
            {
                made = (IGem)_provider.GetRequiredService(gem.Type);
            }
            catch (Exception ex)
            {
                // A missing service, a gem it needs that wasn't made, or a gem that can't start says why in the message;
                // anything else is a bug in the gem, and its stack says where.
                string why = ex is InvalidOperationException ? ex.Message : ex.ToString();
                Log.Error($"Gem {gem.Name} was not made: {why}");
                code.Dispose();
                continue;
            }

            if (made is IEcs ecs)
                _world.Ecs = ecs;

            LoadedGem loaded = new(made, gem);
            _loaded.Add(loaded);
            loaded.Subscriptions.Add(code);
            loaded.Subscriptions.Add(_assets.Register(made));

            // The gem is an IWindow in any mode, but a headless run has no window.
            if (made is IWindow window && !_app.IsHeadless)
            {
                _app.Window = window;

                // The window is the gem, which can't unload while the app holds it.
                loaded.Subscriptions.Add(new Subscription(() => _app.Window = null));
            }

            if (made is ILogSink sink)
                loaded.Subscriptions.Add(Log.Register(sink));

            string what = gem.Description.Length > 0 ? $": {gem.Description}" : string.Empty;
            Log.Info($"Gem {gem.Name} {gem.Version.ToString(3)} by {gem.Author} started{what}");
        }
    }

    /// <summary>
    /// Restarts the run once a build has written a gem's files and then left them alone for a while: a build writes each
    /// gem and its dependencies in turn.
    /// </summary>
    private void RestartIfBuilt()
    {
        long built = Volatile.Read(ref _buildTimestamp);
        if (built == 0 || Stopwatch.GetElapsedTime(built) < _settleTime)
            return;

        Log.Info("A gem's code changed, so the run restarts.");
        _app.Restart();
    }

    /// <summary>
    /// Calls <paramref name="hook"/> on every gem still in the frame, in the order they were made.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever a gem throws, it is logged and the gem leaves the frame; the others go on."
    )]
    private void RunHook(Hook hook, in Frame frame)
    {
        foreach (LoadedGem gem in _loaded)
        {
            if (gem.HasLeft)
                continue;

            try
            {
                switch (hook)
                {
                    case Hook.Update:
                        gem.Gem.Update(in frame);
                        break;
                    case Hook.FixedUpdate:
                        gem.Gem.FixedUpdate(in frame);
                        break;
                    case Hook.LateUpdate:
                        gem.Gem.LateUpdate(in frame);
                        break;
                    case Hook.Render:
                        gem.Gem.Render(in frame);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(hook), hook, "There is no such hook.");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Gem {gem.Name} failed in {hook} and stops running: {ex}");
                gem.HasLeft = true;
            }
        }
    }

    /// <summary>
    /// Watches <paramref name="gem"/>'s folder for a build, which writes its assemblies.
    /// </summary>
    private void Watch(GemInfo gem)
    {
        Result<IFileWatcher> watched = _files.Watch(gem.Folder, "*.dll");
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

    private void Touch() => Volatile.Write(ref _buildTimestamp, Stopwatch.GetTimestamp());

    /// <summary>
    /// The gems the registry found, each after the gems it needs. A gem in a cycle, or one that needs it, is logged and
    /// left out, since the provider hangs making a cycle.
    /// </summary>
    private List<GemInfo> GetSorted()
    {
        List<GemInfo> sorted = [];
        HashSet<string> placed = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> refused = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> visiting = new(StringComparer.OrdinalIgnoreCase);
        foreach (GemInfo gem in _registry.Gems)
        {
            Place(gem, sorted, placed, refused, visiting);
        }

        return sorted;
    }

    /// <summary>
    /// Places <paramref name="gem"/> in <paramref name="sorted"/> after the gems it needs: whether it was.
    /// </summary>
    private bool Place(
        GemInfo gem,
        List<GemInfo> sorted,
        HashSet<string> placed,
        HashSet<string> refused,
        HashSet<string> visiting
    )
    {
        if (placed.Contains(gem.Name))
            return true;

        // A gem visited again before it is placed is in a cycle; the gem that needs it says so.
        if (refused.Contains(gem.Name) || !visiting.Add(gem.Name))
            return false;

        foreach (string name in gem.Needs)
        {
            if (_registry.Find(name) is not { } need || Place(need, sorted, placed, refused, visiting))
                continue;

            string why = visiting.Contains(name) ? "which needs it: a cycle" : "which was not made";
            Log.Error($"Gem {gem.Name} was not made: it needs {name}, {why}.");
            visiting.Remove(gem.Name);
            refused.Add(gem.Name);
            return false;
        }

        visiting.Remove(gem.Name);
        placed.Add(gem.Name);
        sorted.Add(gem);
        return true;
    }

    /// <summary>
    /// A hook of <see cref="IGem"/>'s.
    /// </summary>
    private enum Hook : byte
    {
        Update,
        FixedUpdate,
        LateUpdate,
        Render,
    }
}
