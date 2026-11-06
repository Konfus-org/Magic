using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
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
/// The run's gems: makes the ones registered, in their order, as it is made, drives them through the frame, restarts
/// the run once a build of one has settled, and ends them as it is disposed. A gem that can't be made is logged, and the
/// others go on without it; one that throws in a hook is logged and leaves every hook, and the frame goes on.
/// </summary>
internal sealed class Gems : IDisposable
{
    /// <summary>
    /// How long a gem's files must have been left alone after a build wrote them.
    /// </summary>
    private static readonly TimeSpan _settleTime = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Each <see cref="Hook"/>'s name, made once, as its metric's tag.
    /// </summary>
    private static readonly string[] _hookNames = Enum.GetNames<Hook>();

    private static readonly Histogram<double> _frameDuration = Metrics.Meter.CreateHistogram<double>(
        "magic.core.frame.duration",
        "s",
        "From one frame to the next."
    );

    private static readonly Histogram<double> _frameWorkDuration = Metrics.Meter.CreateHistogram<double>(
        "magic.core.frame.work.duration",
        "s",
        "The main thread's work on a frame, the waits for the GPU and the display among it."
    );

    private static readonly Histogram<double> _hookDuration = Metrics.Meter.CreateHistogram<double>(
        "magic.core.gem.hook.duration",
        "s",
        "One gem's hook."
    );

    private readonly IServiceProvider _provider;

    private readonly App _app;

    private readonly IFileSystem _files;

    private readonly Assets _assets;

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
    /// When the last sleep ended, 0 before the first frame's: where a frame's work starts.
    /// </summary>
    private long _frameStart;

    /// <summary>
    /// Makes the gems <paramref name="registered"/> through <paramref name="provider"/>, which was built from the services
    /// they were registered with.
    /// </summary>
    public Gems(
        List<GemInfo> registered,
        IServiceProvider provider,
        App app,
        IFileSystem files,
        Assets assets,
        World world,
        EventCoordinator events
    )
    {
        _provider = provider;
        _app = app;
        _files = files;
        _assets = assets;
        _world = world;
        _events = events;
        Load(registered);
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
            _loaded[i].Ends.Dispose();
        }

        _loaded.Clear();
    }

    /// <summary>
    /// One frame: restarts the run if a gem was built, works out the clocks, turns what spins, runs every gem's hooks in
    /// order with the fixed steps the clock owes. Presenting to the window sets the pace; with no window, frames run as
    /// fast as they go. Time begins with the first frame, not with what was loaded before it.
    /// </summary>
    public void Pump()
    {
        RestartIfBuilt();

        // While the loading screen shows, time runs as it is, so it moves however the world it replaces was paused.
        bool isPaused = _app.IsPaused || (_world.IsPaused && !_world.IsShowingLoadingScreen);
        float timeScale = _world.IsShowingLoadingScreen ? 1 : _world.TimeScale;
        Frame frame = _clock.Advance(isPaused, timeScale, out int fixedSteps) with { Events = _events.Take() };

        // The first frame's time is the load's before it, not a frame's.
        if (_frameStart != 0)
            _frameDuration.Record(frame.AppDeltaSec);

        _world.TurnSpins((float)frame.SimDeltaSec);
        long hooksStart = Stopwatch.GetTimestamp();
        RunHook(Hook.Update, frame);
        for (int step = 1; step <= fixedSteps; step++)
        {
            RunHook(Hook.FixedUpdate, _clock.GetFixedStep(frame, step));
        }

        _clock.EndFixedSteps(fixedSteps);
        RunHook(Hook.LateUpdate, frame);
        RunHook(Hook.Render, frame);
        _frameWorkDuration.RecordSince(_frameStart == 0 ? hooksStart : _frameStart);
        _frameStart = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Calls <paramref name="hook"/> on <paramref name="gem"/>, which leaves the frame if it throws.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever a gem throws, it is logged and the gem leaves the frame; the others go on."
    )]
    private static void RunHook(Hook hook, in Frame frame, LoadedGem gem)
    {
        try
        {
            switch (hook)
            {
                case Hook.Update:
                    gem.Instance.Update(in frame);
                    break;
                case Hook.FixedUpdate:
                    gem.Instance.FixedUpdate(in frame);
                    break;
                case Hook.LateUpdate:
                    gem.Instance.LateUpdate(in frame);
                    break;
                case Hook.Render:
                    gem.Instance.Render(in frame);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(hook), hook, "There is no such hook.");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Gem {gem.Name} failed in {hook} and stops running: {ex}");
            gem.HasFailed = true;
        }
    }

    /// <summary>
    /// Makes each gem, after registering what it brings so its constructor can find it: its folder of assets, its asset
    /// types and its code, which goes when it does. Then it sets the gem up: the app's window for as long as it lasts when it provides one, the world's ECS, its
    /// place on the log when it is a sink, its hooks into domain loading, and the asset types it reads and writes.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever a gem's constructor throws, it is logged and the gem is not made; the others go on."
    )]
    private void Load(List<GemInfo> registered)
    {
        foreach (GemInfo gem in registered)
        {
            Watch(gem);
            if (gem.Resources is not null)
                _assets.Register(gem.Resources);

            _assets.Register(gem.Type.Assembly);
            Subscription code = Assemblies.Register(gem.Type.Assembly);
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

            LoadedGem loaded = new(
                made,
                gem.Name,
                [
                    new Subscription(gem.Context.Unload),
                    new Subscription(() => (made as IDisposable)?.Dispose()),
                    code,
                    _assets.Register(made),
                ]
            );
            _loaded.Add(loaded);

            // The gem is an IWindow in any mode, but a headless run has no window.
            if (made is IWindow window && !_app.IsHeadless)
            {
                _app.Window = window;

                // The window is the gem, which can't unload while the app holds it.
                loaded.Ends.Add(new Subscription(() => _app.Window = null));
            }

            if (made is ILogSink sink)
                loaded.Ends.Add(Log.Register(sink));

            if (made is IHookIntoDomainLoading hooks)
                loaded.Ends.Add(_world.Register(hooks));

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
    private void RunHook(Hook hook, in Frame frame)
    {
        foreach (LoadedGem gem in _loaded)
        {
            if (gem.HasFailed)
                continue;

            long start = _hookDuration.Start();
            RunHook(hook, frame, gem);
            _hookDuration.RecordSince(
                start,
                new KeyValuePair<string, object?>(Metrics.GemNameTag, gem.Name),
                new KeyValuePair<string, object?>(Metrics.HookTag, _hookNames[(int)hook])
            );
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
    /// A hook of <see cref="IGem"/>'s.
    /// </summary>
    private enum Hook : byte
    {
        Update,
        FixedUpdate,
        LateUpdate,
        Render,
    }

    /// <summary>
    /// A gem as made, and what ends with it, last added first: its subscriptions (its place on the log when it is a
    /// sink, the asset types it reads, the hooks it joined), then the gem itself, then its load context, which frees its
    /// assemblies once nothing refers to the gem.
    /// </summary>
    private sealed record LoadedGem(IGem Instance, string Name, Subscriptions Ends)
    {
        /// <summary>
        /// Whether the gem threw in a hook, so it no longer runs: it is still ended with the others.
        /// </summary>
        public bool HasFailed { get; set; }
    }
}
