using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Reflection;
using Magic.Assets;
using Magic.Configuration;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Events;
using Magic.Files;
using Magic.Graphics;
using Magic.Platform;
using Magic.Threading;
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
    /// How long a fixed step is.
    /// </summary>
    private const double FixedStepSec = 1.0 / 60;

    /// <summary>
    /// The most fixed steps a frame runs; what the simulation owes past them is dropped.
    /// </summary>
    private const int MaxFixedSteps = 4;

    /// <summary>
    /// How long a gem's files must have been left alone after a build wrote them.
    /// </summary>
    private static readonly TimeSpan _settleTime = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Each <see cref="Hook"/>'s name, made once, as its metric's tag.
    /// </summary>
    private static readonly string[] _hookNames = Enum.GetNames<Hook>();

    private static readonly Histogram<double> _frameDuration = Metrics.Meter.CreateHistogram<double>(
        Metrics.FrameDuration,
        "s",
        "From one frame to the next."
    );

    private static readonly Histogram<double> _frameWorkDuration = Metrics.Meter.CreateHistogram<double>(
        Metrics.FrameWorkDuration,
        "s",
        "The game thread's work on a frame."
    );

    private static readonly Histogram<double> _renderFrameDuration = Metrics.Meter.CreateHistogram<double>(
        Metrics.RenderFrameDuration,
        "s",
        "From one frame the render thread draws to the next, the waits for the GPU and the display among it."
    );

    private readonly App _app;

    private readonly World _world;

    private readonly EventCoordinator _events;

    private readonly Threads _threads;

    /// <summary>
    /// The game thread's real time, from its first frame.
    /// </summary>
    private readonly Stopwatch _clock = new();

    private readonly List<LoadedGem> _loaded = [];

    private readonly Rendering _rendering = new();

    private readonly Subscriptions _watches = [];

    /// <summary>
    /// The frame every gem's Render records into, which the GPU provider presents. Render thread.
    /// </summary>
    private readonly RenderGraph _graph = new();

    /// <summary>
    /// The render thread's own real time, from the first frame it draws.
    /// </summary>
    private readonly Stopwatch _renderClock = new();

    /// <summary>
    /// Set by the render thread each time it has drawn a frame, for the game thread waiting to stay one frame ahead.
    /// </summary>
    private readonly AutoResetEvent _drawn = new(initialState: false);

    /// <summary>
    /// The gem whose part is the GPU provider, which presents the frame; null when no gem provides one.
    /// </summary>
    private readonly LoadedGem? _gpuProvider;

    /// <summary>
    /// When a watch last saw a build write a gem's files, 0 when none has: written whole from the watches' threads.
    /// </summary>
    private long _buildTimestamp;

    /// <summary>
    /// When the last wait for the render thread ended, 0 before the first frame's: where a frame's work starts.
    /// </summary>
    private long _frameStart;

    /// <summary>
    /// The game thread's last frame, whose clocks the next one goes on from.
    /// </summary>
    private Frame _frame;

    /// <summary>
    /// The simulation time the fixed steps have reached.
    /// </summary>
    private double _fixedSec;

    /// <summary>
    /// The simulation time the fixed steps still owe; dropped when it is more than <see cref="MaxFixedSteps"/> can run.
    /// </summary>
    private double _owedSec;

    /// <summary>
    /// The last frame the render thread drew, whose clocks the next one goes on from.
    /// </summary>
    private Frame _renderFrame;

    /// <summary>
    /// Registers the gems <paramref name="listed"/> with <paramref name="services"/>, makes them through the provider
    /// built from those, and starts the render thread. Each gem's folder is watched for a build, and
    /// what it brings is registered before it is made, so its constructor can find it: its folder of assets, its asset
    /// types and its code, which goes when it does. Then the gem is set up: the app's window for as long as it lasts
    /// when it provides one, the world's ECS, its settings, its place on the log when it is a sink, the asset types it
    /// reads and writes, and its part of the frame; and, once every gem is made, its hooks into domain loading, unless
    /// it can never render.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever a gem's constructor throws, it is logged and the gem is not made; the others go on."
    )]
    public Gems(
        ImmutableArray<string> listed,
        ServiceCollection services,
        App app,
        Settings settings,
        IFileSystem files,
        AssetManager assets,
        World world,
        EventCoordinator events,
        Threads threads
    )
    {
        _app = app;
        _world = world;
        _events = events;
        _threads = threads;
        List<RegisteredGem> registered = GemRegistry.Register(files, listed, services);

        // Never disposed, since its singletons are the gems, which Dispose ends.
        services.MakeReadOnly();
        IServiceProvider provider = services.BuildServiceProvider();

        // A constructor may load an asset of a type a gem made after it reads.
        assets.Expect(registered.SelectMany(gem => gem.PartTypes));
        foreach (RegisteredGem gem in registered)
        {
            // A build writes the gem's assemblies; the run restarts once it has settled.
            Result<IDisposable> watched = files.Watch(
                gem.Folder,
                "*.dll",
                _ => Volatile.Write(ref _buildTimestamp, Stopwatch.GetTimestamp())
            );
            if (watched.Ok)
                _watches.Add(new Subscription(watched.Value.Dispose));
            else
                Log.Warn($"Gem {gem.Name} is not watched, so a build of it won't restart the run: {watched.Error}");

            if (gem.Resources is not null)
                assets.Register(gem.Resources);

            assets.Register(gem.Type.Assembly);
            Subscription code = Assemblies.Register(gem.Type.Assembly);
            IGem made;
            try
            {
                made = (IGem)provider.GetRequiredService(gem.Type);
            }
            catch (Exception ex)
            {
                // A missing service, a gem it needs that wasn't made, or a gem that can't start says why in the message;
                // anything else is a bug in the gem, and its stack says where.
                string why = ex is InvalidOperationException ? ex.Message : ex.ToString();
                Log.Error($"Gem {gem.Name} was not made: {why}");
                assets.Drop(gem.PartTypes);
                code.Dispose();
                continue;
            }

            LoadedGem loaded = new(
                made,
                [],
                gem.Name,
                [new Subscription(gem.Context.Unload), new Subscription(() => (made as IDisposable)?.Dispose()), code]
            );
            _loaded.Add(loaded);
            foreach (PropertyInfo property in gem.Parts)
            {
                if (property.GetValue(made) is not { } part)
                {
                    Log.Error($"Gem {gem.Name}'s part {property.Name} is null, so nothing it would do is set up.");
                    continue;
                }

                loaded.Parts.Add(part);
                SetUp(gem, part, loaded.Ends, settings, assets);
            }

            // What a part reads was expected by its property's type, which can be a base of the part's class.
            assets.Drop(gem.PartTypes);

            string what = gem.Description.Length > 0 ? $": {gem.Description}" : string.Empty;
            Log.Info($"Gem {gem.Name} {gem.Version.ToString(3)} by {gem.Author} started{what}");
        }

        // Once every gem renders, a gem that must take what no listed gem makes never renders, so it holds no domain's
        // loading back either.
        foreach (LoadedGem loaded in _loaded)
        {
            if (loaded.Parts.Exists(_rendering.HasMissingInput))
                continue;

            foreach (IHookIntoDomainLoading hooks in loaded.Parts.OfType<IHookIntoDomainLoading>())
            {
                loaded.Ends.Add(_world.Register(hooks));
            }
        }

        _gpuProvider = _loaded.Find(static gem => gem.Parts.OfType<IGraphicsApi>().Any());
        _graph.Gpu = _gpuProvider?.Parts.OfType<IGraphicsApi>().First();
        _threads.Start(EngineThread.Render, Render);
    }

    /// <summary>
    /// Stops the render thread, then ends the gems on this one, which is the render thread from then on, last made first,
    /// so no gem outlives one it was made after.
    /// </summary>
    public void Dispose()
    {
        _threads.Stop(EngineThread.Render);
        _drawn.Dispose();
        _watches.Dispose();
        for (int i = _loaded.Count - 1; i >= 0; i--)
        {
            Log.Info($"Unloading {_loaded[i].Name}.");
            _loaded[i].Ends.Dispose();
        }

        _loaded.Clear();
    }

    /// <summary>
    /// One frame: restarts the run if a gem was built, works out the clocks, turns what spins, runs every gem's hooks but
    /// Render in order with the fixed steps the simulation owes, and tells the world the frame is published. Then it waits
    /// until the render thread has drawn the frame before, so the next frame is worked out while this one draws, and the
    /// frames run at the render thread's pace. Time begins with the first frame, not with what was loaded before it.
    /// </summary>
    public void Pump()
    {
        // A build writes each gem and its dependencies in turn, so the run restarts once it left the files alone a
        // while.
        long built = Volatile.Read(ref _buildTimestamp);
        if (built != 0 && Stopwatch.GetElapsedTime(built) >= _settleTime)
        {
            Log.Info("A gem's code changed, so the run restarts.");
            _app.Restart();
        }

        // While the loading screen shows, time runs as it is, so it moves however the world it replaces was paused.
        bool isPaused = _world.IsPaused && !_world.IsShowingLoadingScreen;
        float timeScale = _world.IsShowingLoadingScreen ? 1 : _world.TimeScale;
        Frame frame = CreateFrame(_clock, _frame, isPaused ? 0 : timeScale) with
        {
            IsPaused = isPaused,
            Events = _events.Take(),
        };
        _frame = frame;

        // The first frame's time is the load's before it, not a frame's.
        if (_frameStart != 0)
            _frameDuration.Record(frame.AppDeltaSec);

        _world.TurnSpins((float)frame.SimDeltaSec);
        long hooksStart = Stopwatch.GetTimestamp();
        RunHook(Hook.Update, frame);

        // The fixed steps catch the simulation up with the frame, at most MaxFixedSteps of them, and what is owed past
        // those is dropped. A fixed step sees its own time and no messages.
        _owedSec += frame.SimDeltaSec;
        for (int step = 0; step < MaxFixedSteps && _owedSec >= FixedStepSec; step++)
        {
            _owedSec -= FixedStepSec;
            _fixedSec += FixedStepSec;
            RunHook(
                Hook.FixedUpdate,
                frame with
                {
                    SimSec = _fixedSec,
                    SimDeltaSec = FixedStepSec,
                    Events = FrameEvents.None,
                }
            );
        }

        if (_owedSec >= FixedStepSec)
            _owedSec = 0;

        RunHook(Hook.LateUpdate, frame);
        _world.PublishedFrame = frame.Index;
        _frameWorkDuration.RecordSince(_frameStart == 0 ? hooksStart : _frameStart);

        // The one wait on the render thread in a frame, so the game thread never runs more than a frame ahead of it; the
        // render thread never waits for this one.
        while (_world.DrawnFrame + 1 < frame.Index)
        {
            _drawn.WaitOne();
        }

        _frameStart = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// The frame after <paramref name="last"/> on <paramref name="clock"/>, which starts with the first one. The simulation
    /// moves by the real time since <paramref name="last"/>, times <paramref name="timeScale"/>.
    /// </summary>
    private static Frame CreateFrame(Stopwatch clock, in Frame last, float timeScale)
    {
        bool isFirst = !clock.IsRunning;
        clock.Start();
        double nowSec = clock.Elapsed.TotalSeconds;
        double simDeltaSec = (nowSec - last.AppSec) * timeScale;

        return new()
        {
            Index = isFirst ? 0 : last.Index + 1,
            AppSec = nowSec,
            AppDeltaSec = nowSec - last.AppSec,
            SimSec = last.SimSec + simDeltaSec,
            SimDeltaSec = simDeltaSec,
        };
    }

    /// <summary>
    /// Calls <paramref name="hook"/> on <paramref name="gem"/>, timed, unless it left the frame; it leaves the frame if it
    /// throws. Present is the GPU provider's alone.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever a gem throws, it is logged and the gem leaves the frame; the others go on."
    )]
    private void RunHook(Hook hook, in Frame frame, LoadedGem gem)
    {
        if (gem.HasFailed)
            return;

        long start = Metrics.HookDurations.Start();
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
                case Hook.Present:
                    _graph.Gpu?.Present(_graph.Finish());
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

        Metrics.HookDurations.RecordSince(
            start,
            new KeyValuePair<string, object?>(Metrics.GemNameTag, gem.Name),
            new KeyValuePair<string, object?>(Metrics.HookTag, _hookNames[(int)hook])
        );
    }

    /// <summary>
    /// One frame of the render thread, which runs them as fast as the GPU and the display let it, never waiting for the
    /// game thread: every gem's Render into the frame's graph, then the GPU provider's Present of it. It tells the world
    /// which published frame it drew at least, the gems taking what was published when it began or newer, and wakes the
    /// game thread if it waits for that. A render frame has no messages.
    /// </summary>
    private void Render()
    {
        uint published = _world.PublishedFrame;
        Frame frame = CreateFrame(_renderClock, _renderFrame, timeScale: 1);
        _renderFrame = frame;
        if (frame.Index > 0)
            _renderFrameDuration.Record(frame.AppDeltaSec);

        _rendering.Pump(frame, _graph);
        if (_gpuProvider is not null)
            RunHook(Hook.Present, frame, _gpuProvider);

        // Without a GPU provider, nothing presents what the gems recorded, so it goes.
        _graph.Finish();
        _world.DrawnFrame = published;
        _drawn.Set();
    }

    /// <summary>
    /// Calls <paramref name="hook"/> on every gem still in the frame, in the order they were made.
    /// </summary>
    private void RunHook(Hook hook, in Frame frame)
    {
        foreach (LoadedGem gem in _loaded)
        {
            RunHook(hook, frame, gem);
        }
    }

    /// <summary>
    /// Sets up <paramref name="part"/>, a part the made <paramref name="gem"/> exports, until
    /// <paramref name="ends"/> end: the app's window and platform when it provides them, its settings, its place on the
    /// log when it is a sink, the world's ECS, the asset types it reads and writes, and its part of the frame.
    /// </summary>
    private void SetUp(RegisteredGem gem, object part, Subscriptions ends, Settings settings, AssetManager assets)
    {
        ends.Add(assets.Register(part));

        // The part is an IWindow in any mode, but a headless run has no window.
        if (part is IWindow window && !_app.IsHeadless)
        {
            _app.Window = window;

            // The window is the gem's, which can't unload while the app holds it.
            ends.Add(new Subscription(() => _app.Window = null));
        }

        if (part is IPlatform platform)
        {
            _app.Platform = platform;
            ends.Add(new Subscription(() => _app.Platform = null));
        }

        // A gem whose settings can't be listed still runs by them; only the console and tools can't reach them.
        try
        {
            ends.Add(settings.Register(part));
        }
        catch (InvalidOperationException ex)
        {
            Log.Error($"Gem {gem.Name}'s settings can't be reached: {ex.Message}");
        }

        if (part is ILogSink sink)
            ends.Add(Log.Register(sink));

        if (part is IEcs ecs)
            ends.Add(_world.Register(ecs));

        // A gem that makes what another makes, or closes a cycle, renders nothing; the others go on.
        try
        {
            ends.Add(_rendering.Register(part));
        }
        catch (InvalidOperationException ex)
        {
            Log.Error($"Gem {gem.Name} renders nothing: {ex.Message}");
        }
    }

    /// <summary>
    /// A hook of the frame's: <see cref="IGem"/>'s, and Present, which is the GPU provider's alone.
    /// </summary>
    private enum Hook : byte
    {
        Update,
        FixedUpdate,
        LateUpdate,
        Present,
    }

    /// <summary>
    /// A gem as made, and what ends with it, last added first: its subscriptions (its place on the log when it is a
    /// sink, the asset types it reads, the hooks it joined), then the gem itself, then its load context, which frees its
    /// assemblies once nothing refers to the gem. Its parts are what it exports, which Core sets up; the gem itself has
    /// only its hooks.
    /// </summary>
    private sealed record LoadedGem(IGem Instance, List<object> Parts, string Name, Subscriptions Ends)
    {
        /// <summary>
        /// Whether the gem threw in a hook, so it no longer runs: it is still ended with the others.
        /// </summary>
        public bool HasFailed { get; set; }
    }
}
