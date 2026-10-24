using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Runtime.InteropServices;
using Magic.Contexts.Assets;
using Magic.Contexts.CmdLine;
using Magic.Contexts.ECS;
using Magic.Contexts.Frames;
using Magic.Contexts.Subscriptions;
using Magic.Contexts.Telemetry;
using Magic.Contexts.Threading;
using Magic.Contexts.Utils;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Internal;
using Magic.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Magic;

/// <summary>
/// Runs a project: opens it, makes its gems and its <see cref="World"/>, opens the project's entry point in the world,
/// runs frames until the app quits, and ends the gems. That is all Core does; everything else is a gem. One runs each
/// run, as a restart makes another.
/// <para>
/// The frames run on two threads. The main thread runs a frame's Update, FixedUpdate and LateUpdate, waits until the
/// render thread is done with the frame before, runs Sync, then hands the frame to the render thread for Render and
/// goes on to the next; so the render thread runs at most one frame behind, and Sync is the one point where both hold
/// still: there, first, the settings changed since the last frame are copied into the gems' objects
/// (<see cref="Settings"/>). The main thread's phases run each gem's hook in the order the gems were made, Render in
/// reverse, and only on the gems that implement it. FixedUpdate runs once for every fixed step
/// (<see cref="CoreSettings.FixedRate"/>) of the world's time since the last frame. Each frame starts with what workers
/// finished for the main thread: the work handed to it, the world's arrivals, then the events. While the app is paused
/// (<see cref="App.IsPaused"/>) the frames hold, and every gem idles in their place (<see cref="IGem.Idle"/>). The
/// frames are measured on the meter <c>Magic.Core</c>, which any gem can listen to.
/// </para>
/// </summary>
public sealed class Engine
{
    private static readonly Histogram<double> _frameDuration = Metrics.Meter.CreateTimer(
        Metrics.FrameDuration,
        "Time from one frame's start to the next's.",
        Metrics.FrameBuckets);
    private static readonly Histogram<double> _renderWait = Metrics.Meter.CreateTimer(
        Metrics.RenderWaitDuration,
        "How long the main thread waited for the render thread to finish the frame before, ahead of Sync.",
        Metrics.FrameBuckets);
    private static readonly Histogram<double> _hookDuration = Metrics.Meter.CreateTimer(
        Metrics.HookDuration,
        "How long one gem's hook took, tagged with the gem's name and the phase.",
        Metrics.FrameBuckets);

    /// <summary>
    /// How long the main thread waits between two idles while the app is paused, unless work is handed to it first:
    /// about 60 a second, as often as frames come.
    /// </summary>
    private static readonly TimeSpan _idlePeriod = TimeSpan.FromSeconds(1.0 / 60);

    /// <summary>
    /// The gems, in the order they were made.
    /// </summary>
    private readonly LoopGem[] _gems;

    /// <summary>
    /// For each hook, the gems that implement it, in the order it runs them.
    /// </summary>
    private readonly LoopGem[][] _gemsByHook;

    private readonly World _world;
    private readonly App _app;
    private readonly Threads _threads;
    private readonly Events _events;
    private readonly Settings _settings;
    private readonly CoreSettings _core;
    private readonly Args _args;

    /// <summary>
    /// Set while the render thread has no frame, nor idle: it starts set, is reset as either is handed over and set
    /// again once it is done.
    /// </summary>
    private readonly ManualResetEventSlim _rendered;

    private readonly Action _renderFrame;
    private readonly Action _renderIdle;

    /// <summary>
    /// The frame handed to the render thread, written before it is handed over.
    /// </summary>
    private Frame _frameToRender;

    private Engine(
        List<GemLoader.CreatedGem> gems,
        World world,
        App app,
        Threads threads,
        Events events,
        Settings settings,
        CoreSettings core,
        Args args,
        ManualResetEventSlim rendered)
    {
        _gems = new LoopGem[gems.Count];
        for (int i = 0; i < gems.Count; i++)
        {
            _gems[i] = new LoopGem(gems[i].Gem, gems[i].Name, gems[i].Registrations);
        }

        // Render, the render thread's idle and Stop run in reverse, so a gem finishes what the gems that take it drew,
        // and ends after them.
        Hook[] hooks = Enum.GetValues<Hook>();
        _gemsByHook = new LoopGem[hooks.Length][];
        foreach (Hook hook in hooks)
        {
            bool isReversed = hook is Hook.Render or Hook.RenderIdle or Hook.Stop;
            _gemsByHook[(int)hook] = GetGemsImplementing(_gems, GetMethodName(hook), isReversed);
        }

        _world = world;
        _app = app;
        _threads = threads;
        _events = events;
        _settings = settings;
        _core = core;
        _args = args;
        _rendered = rendered;
        _renderFrame = RenderFrame;
        _renderIdle = IdleRenderThread;
    }

    /// <summary>
    /// Runs the project <paramref name="args"/> names. The calling thread must be the process's first: it becomes the
    /// render thread, which the OS ties windows and their events to, and makes and ends the gems; the frames' game side
    /// runs on a main thread started beside it. Returns once a gem quits the app or its frame or time limit is reached,
    /// or false straight away when the project cannot be opened (the reason is logged). Whatever fails after that is
    /// logged, and the run goes on without it.
    /// <para>
    /// Core's services, the world among them, and every gem and the interfaces each provides, are registered with a
    /// service provider, which makes each gem after whatever its constructor asks for (<see cref="GemLoader"/>). The
    /// assets are made before any gem loads, take each one's folder as it does, and are indexed once they all have. The
    /// settings are made next, as every gem's are made from the presets they read.
    /// </para>
    /// <para>
    /// A headless run has no window to close, so Ctrl+C in the terminal stops it cleanly, ending its gems. Otherwise
    /// Ctrl+C ends the process at once, as it does any program.
    /// </para>
    /// </summary>
    public static bool Run(Args args)
    {
        FileSystem files = new();
        Result<string> found = FindProject(files, args.ProjectPath);
        Result<Project> opened = found.Ok ? ReadProject(files, found.Value) : Result.Failure<Project>(found.Error);
        if (!found.Ok || !opened.Ok)
        {
            Debugging.Error(opened.Error);

            return false;
        }

        Project project = opened.Value;
        string folder = files.GetParent(found.Value);

        // A run that restarts runs again at once, with the command line, the world and the clocks it ended with.
        RunStart? next = new RunStart(args, null, default);
        while (next is { } start)
        {
            next = RunOnce(files, project, folder, start);
        }

        return true;
    }

    /// <summary>
    /// Runs <paramref name="project"/>, in <paramref name="folder"/>, once, until the run ends, from
    /// <paramref name="start"/>: what to run it again with at once when the run restarts (<see cref="App.Restart"/>),
    /// the command line with every setting as it is now, the world as it is and its clocks, else null.
    /// </summary>
    [SuppressMessage(
        "Usage",
        "VSTHRD002:Avoid problematic synchronous waits",
        Justification = "The assets' index is waited for before the run's threads start, and the saved world once "
            + "they have both ended; the index is read on workers, and the world's main-thread part ran before the "
            + "threads ended and the rest is written on a worker, so neither wait needs a thread it holds up.")]
    private static RunStart? RunOnce(FileSystem files, Project project, string folder, RunStart start)
    {
        Args args = start.Args;
        using App app = new(project, folder);
        using Threads threads = new(app);

        // Headless, Ctrl+C quits the app, and its gems end; with a window it ends the process at once, as in any
        // program.
        using PosixSignalRegistration? interrupt = args.IsHeadless
            ? PosixSignalRegistration.Create(
                PosixSignal.SIGINT,
                context =>
                {
                    context.Cancel = true;
                    app.Quit("Ctrl+C");
                })
            : null;

        Debugging.Info($"Running {project.Name} from {folder}");
        Events events = new();
        Assemblies assemblies = new();
        Json.Assemblies = assemblies;
        using Assets assets = new(files, app, events);
        ServiceCollection services = CreateCoreServices(
            files,
            app,
            threads,
            events,
            args,
            assemblies,
            assets);

        // The project's code is taken in before the gems, so files can name its components and scripts too.
        using CodeReloader code = new(files, project, assemblies, assets);
        using GemLoader loader = new(services, files, project, assemblies, assets);

        // Not disposed: the gems are ended below one at a time, so one that throws cannot stop the rest ending.
        ServiceProvider container = services.BuildServiceProvider();

        // The settings and the gems find assets as they are made, so every folder is indexed first: each has been read
        // on a worker since it was added, as the gems loaded. From then on the assets read files with what the gems
        // provide.
        assets.IndexAsync(container).GetAwaiter().GetResult();
        Settings settings = container.GetRequiredService<Settings>();
        CoreSettings core = CreateCoreSettings(settings);
        List<GemLoader.CreatedGem> gems = loader.Load(container);
        World world = container.GetRequiredService<World>();
        List<IPreloader> preloaders = [];
        foreach (GemLoader.CreatedGem created in gems)
        {
            if (created.Gem is IPreloader preloader) preloaders.Add(preloader);
        }

        world.SetPreloaders(preloaders);

        using ManualResetEventSlim rendered = new(initialState: true);
        Engine engine = new(gems, world, app, threads, events, settings, core, args, rendered);

        // Both engine threads have ended, so waiting here holds up no work of theirs.
        return engine.RunThreads(start.State, start.Clocks) is { } restart
            ? new RunStart(restart.Args, restart.Saving.GetAwaiter().GetResult(), restart.Clocks)
            : null;
    }

    /// <summary>
    /// The engine's own settings, made under <c>Core</c> before any gem's, so they come first wherever every setting is
    /// listed; their defaults when the presets give them values that do not fit, which is an error.
    /// </summary>
    private static CoreSettings CreateCoreSettings(Settings settings)
    {
        Result<ISettings?> made = settings.Create("Core", typeof(CoreSettings));

        if (made is { Ok: true, Value: CoreSettings core }) return core;

        Debugging.Error($"Core's settings are left at their defaults: {made.Error}");

        return new CoreSettings();
    }

    /// <summary>
    /// Reads the project's .magic file at <paramref name="file"/>.
    /// </summary>
    private static Result<Project> ReadProject(FileSystem files, string file)
    {
        Result<byte[]> bytes = files.Read(file);

        if (!bytes.Ok) return Result.Failure<Project>(bytes.Error);

        Result<Project> read = Json.Read<Project>(bytes.Value);

        return read.Ok ? read : Result.Failure<Project>($"{file} is not a valid project: {read.Error}");
    }

    /// <summary>
    /// The project's .magic file at <paramref name="path"/>: that file, or the one in the folder there, which must hold
    /// exactly one.
    /// </summary>
    private static Result<string> FindProject(FileSystem files, string path)
    {
        if (files.FileExists(path)) return Result.Success(path);

        if (!files.FolderExists(path))
            return Result.Failure<string>($"There is no project at {path}: it is neither a .magic file nor a folder.");

        IReadOnlyList<string> found = files.Find(path, ".magic");

        if (found.Count != 1)
        {
            return Result.Failure<string>(
                $"The project folder {path} must hold exactly one .magic file; it holds {found.Count}.");
        }

        return Result.Success(found[0]);
    }

    /// <summary>
    /// What a gem's constructor can ask for of Core, before what the gems provide: the settings and the cache are made
    /// once every gem's assembly has been loaded. The world is made once a gem asks for it, or once every gem is, over
    /// the ECS and the scripts the gems provide, when they do. The code loaded, <paramref name="assemblies"/>, is a
    /// service too: the gem that reads <c>.cs</c> files finds a script's class in it. So are the
    /// <paramref name="assets"/>, made before the gems load, as each one's folder is added to them as it does.
    /// </summary>
    private static ServiceCollection CreateCoreServices(
        IFileSystem files,
        App app,
        Threads threads,
        Events events,
        Args args,
        Assemblies assemblies,
        Assets assets)
    {
        ServiceCollection services = new();
        services.AddSingleton(app);
        services.AddSingleton(assemblies);
        services.AddSingleton(assets);
        services.AddSingleton(files);
        services.AddSingleton(threads);
        services.AddSingleton(events);
        services.AddSingleton(args);
        services.AddSingleton(_ => new Settings(assets, files, events, app, args));
        services.AddSingleton(provider => new Caches(files, provider, app));
        services.AddSingleton(provider => new World(
            provider.GetService<IEcs>(),
            provider.GetService<IScripts>(),
            assemblies,
            assets,
            threads,
            events,
            app));

        return services;
    }

    /// <summary>
    /// Calls <paramref name="gem"/>'s <paramref name="hook"/>; Idle and Stop take no frame.
    /// </summary>
    private static void CallHook(IGem gem, Hook hook, in Frame frame)
    {
        switch (hook)
        {
            case Hook.Update:
                gem.Update(frame);
                break;
            case Hook.FixedUpdate:
                gem.FixedUpdate(frame);
                break;
            case Hook.LateUpdate:
                gem.LateUpdate(frame);
                break;
            case Hook.Sync:
                gem.Sync(frame);
                break;
            case Hook.Render:
                gem.Render(frame);
                break;
            case Hook.MainIdle:
                gem.Idle(EngineThread.Main);
                break;
            case Hook.RenderIdle:
                gem.Idle(EngineThread.Render);
                break;
            case Hook.Stop:
                gem.Stop();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(hook), hook, "There is no such hook.");
        }
    }

    /// <summary>
    /// The name of <see cref="IGem"/>'s method <paramref name="hook"/> calls, which its time is tagged with.
    /// </summary>
    private static string GetMethodName(Hook hook) => hook switch
    {
        Hook.Update => nameof(IGem.Update),
        Hook.FixedUpdate => nameof(IGem.FixedUpdate),
        Hook.LateUpdate => nameof(IGem.LateUpdate),
        Hook.Sync => nameof(IGem.Sync),
        Hook.Render => nameof(IGem.Render),
        Hook.MainIdle or Hook.RenderIdle => nameof(IGem.Idle),
        Hook.Stop => nameof(IGem.Stop),
        _ => throw new ArgumentOutOfRangeException(nameof(hook), hook, "There is no such hook."),
    };

    /// <summary>
    /// Those of <paramref name="gems"/> that implement the hook named <paramref name="hook"/>, in the order the gems
    /// were made, or the reverse when <paramref name="isReversed"/>. A hook a gem leaves to <see cref="IGem"/>'s own
    /// does nothing, so it is never called, nor timed.
    /// </summary>
    private static LoopGem[] GetGemsImplementing(LoopGem[] gems, string hook, bool isReversed)
    {
        List<LoopGem> implementing = [];
        for (int turn = 0; turn < gems.Length; turn++)
        {
            LoopGem gem = gems[isReversed ? gems.Length - 1 - turn : turn];

            // Only the interface map tells a hook the gem's class has from IGem's own, a default method that does
            // nothing, which the class leaves to it.
            InterfaceMapping map = gem.Gem.GetType().GetInterfaceMap(typeof(IGem));
            for (int method = 0; method < map.InterfaceMethods.Length; method++)
            {
                if (string.Equals(map.InterfaceMethods[method].Name, hook, StringComparison.Ordinal)
                    && map.TargetMethods[method].DeclaringType != typeof(IGem))
                {
                    implementing.Add(gem);
                }
            }
        }

        return [.. implementing];
    }

    /// <summary>
    /// Runs the frames on a new thread, the main thread, and the render thread's work on the calling thread, the render
    /// thread, until the main thread hands it its end; then ends the gems here. What to run again with when the app
    /// restarts, else null.
    /// </summary>
    private Restart? RunThreads(WorldState? state, Clocks clocks)
    {
        // Written by the main thread before it ends, and read once it has.
        Restart? restart = null;
        Thread main = new(() => restart = RunMain(state, clocks)) { Name = "Main" };
        main.Start();
        _threads.RunUntilEnded(EngineThread.Render);
        EndGems();
        main.Join();

        // Only now: while the gems dispose, the main thread's is still nobody else's.
        _threads.Release(EngineThread.Main);

        return restart;
    }

    /// <summary>
    /// The main thread: opens the world, runs the frames from <paramref name="clocks"/> until the app quits, saves the
    /// world when it restarts, and stops the gems; then runs what it was left, takes no more, and hands the render
    /// thread its end. What to run again with when the app restarts, else null.
    /// </summary>
    private Restart? RunMain(WorldState? state, Clocks clocks)
    {
        _threads.Claim(EngineThread.Main);
        try
        {
            // Before the first frame, once every gem is made, so every gem's loaders are there to read the domain: the
            // world as the run before left it, else the project's entry point, or, baking, each of the project's
            // domains in turn.
            if (state is { } saved) _world.Restore(saved);
            else if (_args.IsBaking) _world.BakeAsync().FireAndForget();
            else _world.OpenEntryPointAsync().FireAndForget();

            Clocks ended = RunFrames(clocks);

            // Once the last frame is done: the save takes the world here and now, before what the ending run has
            // handed this thread, a cancelled open closing its domain among it, and before the gems end the world's
            // scripts. The JSON is then written on a worker from what was taken, so it may outlast the gems. Quitting
            // is cancelled already, as the run is ending, so the save runs on no token.
            Restart? restart = _app.IsRestarting
                ? new Restart(_settings.GetRestartArgs(), _world.SaveAsync(null, CancellationToken.None), ended)
                : null;

            // Each gem ends what it owns of this thread's, the scripts among it, after the save took them and before
            // the gems are disposed on the render thread.
            RunHook(Hook.Stop, default);

            return restart;
        }
        finally
        {
            // Nothing more is taken, then what came before that runs; the render thread's end is handed last, so
            // everything handed to it before the frames ended still runs.
            _threads.End(EngineThread.Main);
            _threads.Drain(EngineThread.Main);
            _threads.TryPost(EngineThread.Render, () => _threads.End(EngineThread.Render));
        }
    }

    /// <summary>
    /// Runs frames from <paramref name="clocks"/>, both clocks as the run before left them or 0, until the app quits,
    /// the frame limit is reached or the time limit is, then waits for the last to be rendered; while the app is
    /// paused, holds them (<see cref="HoldFrames"/>). The time limit counts from the real clock the run went on from,
    /// the time the app was paused included, so a run left paused still ends. Both clocks as of the last frame started,
    /// which a run started again goes on from. On the main thread.
    /// </summary>
    private Clocks RunFrames(Clocks clocks)
    {
        long startTimestamp = Stopwatch.GetTimestamp();
        double limitSec = _args.TimeLimit.TotalSeconds - clocks.RealSec;

        // The time the fixed steps have reached on the world's clock.
        double fixedSec = clocks.WorldSec;

        // Seconds since the start as of the last frame started, or as a hold ended, which the next frame's real time is
        // measured from, so the time the app was paused is left out of it.
        double previousSec = 0;
        for (ulong index = 0; !_app.IsQuitting; index++)
        {
            if (index == _args.FrameLimit)
            {
                _app.Quit($"ran {_args.FrameLimit} frames");

                break;
            }

            bool isHeld = _app.IsPaused;
            bool isStep = isHeld && HoldFrames(startTimestamp, limitSec);

            if (_app.IsQuitting) break;

            double nowSec = Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds;
            if (nowSec >= limitSec)
            {
                _app.Quit($"ran {_args.TimeLimit.TotalSeconds} s");

                break;
            }

            // The time the app was paused is left out, so a frame after a hold starts afresh, and a stepped one moves
            // the clocks on by one fixed step, however long the app was paused. The fixed step is read once, so the
            // frame and its steps agree on it.
            double fixedStepSec = 1.0 / _core.FixedRate;
            double realDeltaSec = isHeld ? 0 : nowSec - previousSec;
            if (isStep) realDeltaSec = fixedStepSec;

            if (index > 0 && !isHeld) _frameDuration.Record(realDeltaSec);

            previousSec = nowSec;
            Frame frame = CreateFrame(index, realDeltaSec, clocks);
            clocks = new Clocks(frame.RealSec, frame.WorldSec);
            Debugging.Frame = index;

            _threads.Drain(EngineThread.Main);
            _world.Pump(index);
            _events.Deliver();

            RunHook(Hook.Update, frame);
            fixedSec = RunFixedSteps(frame, fixedSec, fixedStepSec);
            RunHook(Hook.LateUpdate, frame);
            WaitForRender();

            // The render thread is done with the frame before, so both hold still: the settings changed since are
            // copied in, then the gems swap what Render reads.
            _settings.ApplyChanges();
            RunHook(Hook.Sync, frame);

            _frameToRender = frame;
            HandToRenderThread(_renderFrame);
        }

        WaitForRender();

        return clocks;
    }

    /// <summary>
    /// Ends the gems, the world and the settings, on the render thread once it has run what it was left.
    /// </summary>
    private void EndGems()
    {
        Debugging.Frame = null;
        DisposeGems();
        _world.Dispose();
        _settings.Dispose();
    }

    /// <summary>
    /// Disposes the gems that are <see cref="IDisposable"/>, the last made first, so each ends before what it uses. What
    /// is registered for a gem ends first: a gem that is an <see cref="ILogSink"/> is taken off the log once it has had
    /// every line logged before, so it can close what it writes to.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever one gem throws while ending, the others still end.")]
    private void DisposeGems()
    {
        for (int i = _gems.Length - 1; i >= 0; i--)
        {
            IGem gem = _gems[i].Gem;
            _gems[i].Registrations.Dispose();

            if (gem is not IDisposable disposable) continue;

            try
            {
                disposable.Dispose();
            }
            catch (Exception ex)
            {
                Debugging.Error($"Gem {_gems[i].Name} threw while disposing: {ex}");
            }
        }
    }

    /// <summary>
    /// Holds the frames while the app is paused (<see cref="App.IsPaused"/>), until it goes on, quits or steps a frame,
    /// or <paramref name="limitSec"/> seconds have passed since <paramref name="startTimestamp"/>: about 60 times a
    /// second, the main thread runs what it was handed, then every gem idles, on the main thread and then on the render
    /// thread (<see cref="IGem.Idle"/>). Whether a frame was stepped. On the main thread.
    /// </summary>
    private bool HoldFrames(long startTimestamp, double limitSec)
    {
        while (_app.IsPaused && !_app.IsQuitting)
        {
            if (_app.TryTakeStep()) return true;

            if (Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds >= limitSec) return false;

            _threads.Wait(EngineThread.Main, _idlePeriod);
            _threads.Drain(EngineThread.Main);
            RunHook(Hook.MainIdle, default);

            // One idle at a time on the render thread, after the frame before, so a main thread woken often by work it
            // is handed hands the render thread no backlog.
            _rendered.Wait();
            HandToRenderThread(_renderIdle);
        }

        return false;
    }

    /// <summary>
    /// The frame <paramref name="index"/>, after <paramref name="clocks"/>: the real clock moved on by
    /// <paramref name="realDeltaSec"/>, and the world's by as much, scaled (<see cref="World.TimeScale"/>), or not at
    /// all while the world is paused (<see cref="World.IsPaused"/>). While a domain loads in place of the world, the
    /// world's clock runs as the real one, unpaused, as the gems ready the new one on the frames' fixed steps.
    /// </summary>
    private Frame CreateFrame(ulong index, double realDeltaSec, Clocks clocks)
    {
        bool isLoading = _world.IsLoading;
        bool isPaused = _world.IsPaused && !isLoading;
        float scale = isLoading ? 1 : _world.TimeScale;
        double worldDeltaSec = isPaused ? 0 : realDeltaSec * scale;

        return new Frame(
            index,
            (float)worldDeltaSec,
            clocks.WorldSec + worldDeltaSec,
            (float)realDeltaSec,
            clocks.RealSec + realDeltaSec,
            isPaused);
    }

    /// <summary>
    /// Runs FixedUpdate for every <paramref name="fixedStepSec"/> from <paramref name="fixedSec"/>, the time the fixed
    /// steps have reached on the world's clock, up to <paramref name="frame"/>'s, each given the step and the time it
    /// reaches; the time they reach now. Past <see cref="CoreSettings.MaxFixedSteps"/>, the steps left are dropped. No
    /// cap can save a step that takes longer than the time it simulates: the loop then runs the cap's worth every frame
    /// and the world falls behind real time, and the step is what must be fixed.
    /// </summary>
    private double RunFixedSteps(in Frame frame, double fixedSec, double fixedStepSec)
    {
        for (int step = 0; fixedSec + fixedStepSec <= frame.WorldSec; step++)
        {
            if (step == _core.MaxFixedSteps) return frame.WorldSec;

            fixedSec += fixedStepSec;
            RunHook(Hook.FixedUpdate, frame with { WorldDeltaSec = (float)fixedStepSec, WorldSec = fixedSec });
        }

        return fixedSec;
    }

    /// <summary>
    /// Hands <paramref name="work"/>, a frame or an idle, to the render thread, which sets <see cref="_rendered"/> once
    /// it is done. When the render thread takes no more, as the run ends, the work is dropped, and nothing is left
    /// waiting for it.
    /// </summary>
    private void HandToRenderThread(Action work)
    {
        _rendered.Reset();

        if (_threads.TryPost(EngineThread.Render, work)) return;

        _rendered.Set();
        Debugging.Warn("Work was handed to the render thread after the run ended; it is dropped.");
    }

    /// <summary>
    /// The frame handed over, on the render thread.
    /// </summary>
    private void RenderFrame()
    {
        try
        {
            RunHook(Hook.Render, _frameToRender);
        }
        finally
        {
            _rendered.Set();
        }
    }

    /// <summary>
    /// The idle handed over while the app is paused, on the render thread.
    /// </summary>
    private void IdleRenderThread()
    {
        try
        {
            RunHook(Hook.RenderIdle, default);
        }
        finally
        {
            _rendered.Set();
        }
    }

    private void WaitForRender()
    {
        long startTimestamp = _renderWait.Start();
        _rendered.Wait();
        _renderWait.RecordSince(startTimestamp);
    }

    /// <summary>
    /// Calls <paramref name="hook"/> on every gem that implements it and is still in the loop, timing each while
    /// something listens. A gem that throws is logged and leaves the frame loop for good, on both threads; the frame
    /// goes on without it. Stop runs on a gem that left the loop too, since what it owns of the main thread's still
    /// ends.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever a gem throws, it is logged and the gem leaves the loop; the engine goes on.")]
    private void RunHook(Hook hook, in Frame frame)
    {
        string name = GetMethodName(hook);
        foreach (LoopGem loopGem in _gemsByHook[(int)hook])
        {
            if (loopGem.HasLeft && hook != Hook.Stop) continue;

            long startTimestamp = _hookDuration.Start();
            try
            {
                CallHook(loopGem.Gem, hook, frame);
            }
            catch (Exception ex)
            {
                Debugging.Error($"Gem {loopGem.Name} threw in {name} and left the frame loop: {ex}");
                loopGem.HasLeft = true;

                continue;
            }

            // Outside the try: a listener that throws is its own failure, not this gem's.
            _hookDuration.RecordSince(
                startTimestamp,
                new(Metrics.GemNameTag, loopGem.Name),
                new(Metrics.FramePhaseTag, name));
        }
    }

    /// <summary>
    /// A gem hook the loop runs over the gems that implement it: the frame's phases, in <see cref="Phase"/>'s order,
    /// Idle on each thread while the app is paused, and Stop once the last frame is done.
    /// </summary>
    private enum Hook
    {
        Update,
        FixedUpdate,
        LateUpdate,
        Sync,
        Render,
        MainIdle,
        RenderIdle,
        Stop,
    }

    /// <summary>
    /// Both clocks a frame tells (<see cref="Frame"/>), in seconds: the real one and the world's.
    /// </summary>
    private readonly record struct Clocks(double RealSec, double WorldSec);

    /// <summary>
    /// What a run starts from: its command line, the world as the run before left it, else null, and both clocks, 0
    /// for the first run.
    /// </summary>
    private readonly record struct RunStart(Args Args, WorldState? State, Clocks Clocks);

    /// <summary>
    /// What to run again with once the threads have ended, when the app restarts: the command line with every setting
    /// as it is now, the world being saved, and both clocks as the last frame left them.
    /// </summary>
    private readonly record struct Restart(Args Args, Task<WorldState> Saving, Clocks Clocks);

    /// <summary>
    /// A gem in the frame loop, by the name the project lists it as, which its assembly is named for, what is
    /// registered for it, and whether it threw and left the loop for good: written and read on both threads.
    /// </summary>
    private sealed class LoopGem(IGem gem, string name, SubscriptionList registrations)
    {
        public IGem Gem { get; } = gem;

        public string Name { get; } = name;

        public SubscriptionList Registrations { get; } = registrations;

        public bool HasLeft
        {
            get => Volatile.Read(ref field);
            set => Volatile.Write(ref field, value);
        }
    }
}
