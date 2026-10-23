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
/// reverse, and only on the gems that implement it. FixedUpdate runs once for every <see cref="FixedStep"/> since the
/// last frame. Each frame starts with what workers finished for the main thread: the work handed to it, the world's
/// arrivals, then the events. The frames are measured on the meter <c>Magic.Core</c>, which any gem can listen to.
/// </para>
/// </summary>
public sealed class Engine
{
    /// <summary>
    /// The time FixedUpdate moves the world on by: 60 steps a second.
    /// </summary>
    private const double FixedStep = 1.0 / 60;

    /// <summary>
    /// The most fixed steps one frame runs, to catch up after a slow one: a frame further behind drops the rest, so
    /// the world loses that time rather than the frame growing to make it up and the next falling further behind.
    /// Four is 67 ms of catch-up, enough for an ordinary hitch, while a frame that takes it all is bounded at four
    /// steps' cost. No cap can save a step that takes longer than the time it simulates: the loop then runs the cap's
    /// worth every frame and the world falls behind real time, and the step is what must be fixed.
    /// </summary>
    private const int MaxFixedSteps = 4;

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
    private static readonly string[] _phases = Enum.GetNames<Phase>();

    /// <summary>
    /// The gems, in the order they were made.
    /// </summary>
    private readonly Hooked[] _gems;

    /// <summary>
    /// For each phase, the gems that implement its hook, in the order the phase runs them.
    /// </summary>
    private readonly Hooked[][] _hooked;

    /// <summary>
    /// The gems that implement Stop, in reverse order: every one of them, a gem that left the loop included, since
    /// what it owns of the main thread's still ends.
    /// </summary>
    private readonly Hooked[] _stops;

    private readonly World _world;
    private readonly App _app;
    private readonly Threads _threads;
    private readonly Events _events;
    private readonly Settings _settings;
    private readonly Args _args;

    /// <summary>
    /// Set while the render thread has no frame: it starts set, is reset as a frame is handed over and set again once
    /// its Render is done.
    /// </summary>
    private readonly ManualResetEventSlim _rendered;

    private readonly Action _renderFrame;

    /// <summary>
    /// The frame handed to the render thread, written before it is handed over.
    /// </summary>
    private Frame _rendering;

    /// <summary>
    /// The time the fixed steps have reached. Main thread.
    /// </summary>
    private double _fixedSeconds;

    /// <summary>
    /// The frame clock (<see cref="Frame.Seconds"/>) as of the last frame started, from where the run before left it
    /// when this one goes on from it, else 0: what a run started again goes on from. Main thread.
    /// </summary>
    private double _seconds;

    /// <summary>
    /// What to run again with once the threads have ended, when the app restarts: the command line with every setting
    /// as it is now, the world being saved, and the frame clock. Written on the main thread before it ends.
    /// </summary>
    private (Args Args, Task<WorldState> Saving, double Seconds)? _restart;

    private Engine(
        List<(IGem Gem, SubscriptionList Registrations)> gems,
        World world,
        App app,
        Threads threads,
        Events events,
        Settings settings,
        Args args,
        double startSeconds,
        ManualResetEventSlim rendered)
    {
        _gems = new Hooked[gems.Count];
        for (int i = 0; i < gems.Count; i++)
        {
            Type type = gems[i].Gem.GetType();
            _gems[i] = new Hooked(gems[i].Gem, type.Assembly.GetName().Name ?? type.Name, gems[i].Registrations);
        }

        _hooked = new Hooked[_phases.Length][];
        for (int phase = 0; phase < _phases.Length; phase++)
        {
            _hooked[phase] = GetHooked(_gems, _phases[phase], isReversed: phase == (int)Phase.Render);
        }

        _stops = GetHooked(_gems, nameof(IGem.Stop), isReversed: true);
        _world = world;
        _app = app;
        _threads = threads;
        _events = events;
        _settings = settings;
        _args = args;
        _rendered = rendered;
        _renderFrame = RenderFrame;
        _fixedSeconds = startSeconds;
        _seconds = startSeconds;
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

        // A run that restarts runs again at once, with the command line, the world and the frame clock it ended with.
        (Args Args, WorldState? State, double Seconds)? next = (args, null, 0);
        while (next is { } run)
        {
            next = RunOnce(files, project, folder, run.Args, run.State, run.Seconds);
        }

        return true;
    }

    /// <summary>
    /// Runs <paramref name="project"/>, in <paramref name="folder"/>, once, until the run ends, going on from
    /// <paramref name="state"/>, the world as the run before left it, and <paramref name="seconds"/>, its frame clock,
    /// when there is one: what to run it again with at once when the run restarts (<see cref="App.Restart"/>), the
    /// command line with every setting as it is now, the world as it is and its frame clock, else null.
    /// </summary>
    [SuppressMessage(
        "Usage",
        "VSTHRD002:Avoid problematic synchronous waits",
        Justification = "The assets' index is waited for before the run's threads start, and the saved world once "
            + "they have both ended; the index is read on workers, and the world's main-thread part ran before the "
            + "threads ended and the rest is written on a worker, so neither wait needs a thread it holds up.")]
    private static (Args Args, WorldState? State, double Seconds)? RunOnce(
        FileSystem files,
        Project project,
        string folder,
        Args args,
        WorldState? state,
        double seconds)
    {
        using App app = new(project, folder);
        using Threads threads = new(app);

        // Headless, Ctrl+C quits the app, and its gems end; with a window it ends the process at once, as in any
        // program.
        using PosixSignalRegistration? interrupt = args.Headless
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
        using Assets assets = new(files, app, events);
        ServiceCollection services = CreateCoreServices(
            files,
            app,
            threads,
            events,
            args,
            assemblies,
            assets);
        using GemLoader loader = new(services, files, project, assemblies, assets);

        // Not disposed: the gems are ended below one at a time, so one that throws cannot stop the rest ending.
        ServiceProvider container = services.BuildServiceProvider();
        assets.Services = container;

        // The settings and the gems find assets as they are made, so every folder is indexed first: each has been read
        // on a worker since it was added, as the gems loaded.
        assets.IndexAsync().GetAwaiter().GetResult();
        Settings settings = container.GetRequiredService<Settings>();
        List<(IGem Gem, SubscriptionList Registrations)> gems = loader.Load(container);
        World world = container.GetRequiredService<World>();
        List<IPreloader> preloaders = [];
        foreach ((IGem gem, _) in gems)
        {
            if (gem is IPreloader preloader) preloaders.Add(preloader);
        }

        world.Preloaders = preloaders;

        using ManualResetEventSlim rendered = new(initialState: true);
        Engine engine = new(gems, world, app, threads, events, settings, args, seconds, rendered);
        (Args Args, Task<WorldState> Saving, double Seconds)? next = engine.RunThreads(state);

        // Both engine threads have ended, so waiting here holds up no work of theirs.
        return next is { } restart ? (restart.Args, restart.Saving.GetAwaiter().GetResult(), restart.Seconds) : null;
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
    /// Calls <paramref name="gem"/>'s hook for <paramref name="phase"/>.
    /// </summary>
    private static void Call(IGem gem, Phase phase, in Frame frame)
    {
        switch (phase)
        {
            case Phase.Update:
                gem.Update(frame);
                break;
            case Phase.FixedUpdate:
                gem.FixedUpdate(frame);
                break;
            case Phase.LateUpdate:
                gem.LateUpdate(frame);
                break;
            case Phase.Sync:
                gem.Sync(frame);
                break;
            case Phase.Render:
                gem.Render(frame);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(phase), phase, "There is no such phase.");
        }
    }

    /// <summary>
    /// Those of <paramref name="gems"/> that implement the hook named <paramref name="name"/>, in the order the gems
    /// were made, or the reverse when <paramref name="isReversed"/>: Render and Stop, so a gem finishes what the gems
    /// that take it drew, and ends after them. A hook a gem leaves to <see cref="IGem"/>'s own does nothing, so it is
    /// never called, nor timed.
    /// </summary>
    private static Hooked[] GetHooked(Hooked[] gems, string name, bool isReversed)
    {
        List<Hooked> hooked = [];
        for (int turn = 0; turn < gems.Length; turn++)
        {
            Hooked gem = gems[isReversed ? gems.Length - 1 - turn : turn];

            // Only the interface map tells a hook the gem's class has from IGem's own, a default method that does
            // nothing, which the class leaves to it.
            InterfaceMapping map = gem.Gem.GetType().GetInterfaceMap(typeof(IGem));
            for (int method = 0; method < map.InterfaceMethods.Length; method++)
            {
                if (string.Equals(map.InterfaceMethods[method].Name, name, StringComparison.Ordinal)
                    && map.TargetMethods[method].DeclaringType != typeof(IGem))
                {
                    hooked.Add(gem);
                }
            }
        }

        return [.. hooked];
    }

    /// <summary>
    /// Runs the frames on a new thread, the main thread, and the render thread's work on the calling thread, the render
    /// thread, until the main thread hands it its end; then ends the gems here. What to run again with when the app
    /// restarts, else null.
    /// </summary>
    private (Args Args, Task<WorldState> Saving, double Seconds)? RunThreads(WorldState? state)
    {
        Thread main = new(() => RunMain(state)) { Name = "Main" };
        main.Start();
        _threads.RunUntilEnded(EngineThread.Render);
        EndGems();
        main.Join();

        // Only now: while the gems dispose, the main thread's is still nobody else's.
        _threads.Release(EngineThread.Main);

        return _restart;
    }

    /// <summary>
    /// The main thread: opens the world, runs the frames until the app quits, saves the world when it restarts, and
    /// stops the gems; then runs what it was left, takes no more, and hands the render thread its end.
    /// </summary>
    private void RunMain(WorldState? state)
    {
        _threads.Claim(EngineThread.Main);
        try
        {
            // Before the first frame, once every gem is made, so every gem's loaders are there to read the domain: the
            // world as the run before left it, else the project's entry point, or, baking, each of the project's
            // domains in turn.
            if (state is { } saved) _world.Restore(saved);
            else if (_args.Bake) _world.BakeAsync().FireAndForget();
            else _world.OpenEntryPointAsync().FireAndForget();

            RunFrames();

            // Once the last frame is done: the save takes the world here and now, before what the ending run has
            // handed this thread, a cancelled open closing its domain among it, and before the gems end the world's
            // scripts. The JSON is then written on a worker from what was taken, so it may outlast the gems. Quitting
            // is cancelled already, as the run is ending, so the save runs on no token.
            if (_app.IsRestarting)
                _restart = (_settings.RestartArgs(), _world.SaveAsync(null, CancellationToken.None), _seconds);

            // Each gem ends what it owns of this thread's, the scripts among it, after the save took them and before
            // the gems are disposed on the render thread.
            StopGems();
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
    /// Runs frames until the app quits, the frame limit is reached or the frame clock reaches the time limit, then
    /// waits for the last to be rendered. On the main thread.
    /// </summary>
    private void RunFrames()
    {
        long start = Stopwatch.GetTimestamp();
        double began = _seconds;
        double previousSeconds = began;
        for (ulong index = 0; !_app.IsQuitting; index++)
        {
            if (index == _args.FrameLimit)
            {
                _app.Quit($"ran {_args.FrameLimit} frames");

                break;
            }

            double seconds = began + Stopwatch.GetElapsedTime(start).TotalSeconds;
            if (seconds >= _args.TimeLimit.TotalSeconds)
            {
                _app.Quit($"ran {_args.TimeLimit.TotalSeconds} s");

                break;
            }

            Frame frame = new(index, (float)(seconds - previousSeconds), seconds);
            if (index > 0) _frameDuration.Record(seconds - previousSeconds);

            previousSeconds = seconds;
            _seconds = seconds;
            Debugging.Frame = index;

            _threads.Drain(EngineThread.Main);
            _world.Pump();
            _events.Deliver();

            RunPhase(Phase.Update, frame);
            RunFixedSteps(frame);
            RunPhase(Phase.LateUpdate, frame);
            WaitForRender();

            // The render thread is done with the frame before, so both hold still: the settings changed since are
            // copied in, then the gems swap what Render reads.
            _settings.ApplyChanges();
            RunPhase(Phase.Sync, frame);

            _rendering = frame;
            _rendered.Reset();
            if (!_threads.TryPost(EngineThread.Render, _renderFrame))
                Debugging.Warn("A frame was handed to the render thread after the run ended; it is dropped.");
        }

        WaitForRender();
    }

    /// <summary>
    /// Runs Stop on every gem that implements it, in reverse order, each timed while something listens; a gem that
    /// throws is logged, and the rest still stop. On the main thread, once the last frame is done and the world is
    /// saved.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever a gem throws as it stops, it is logged and the rest still stop.")]
    private void StopGems()
    {
        foreach (Hooked hooked in _stops)
        {
            long start = _hookDuration.Start();
            try
            {
                hooked.Gem.Stop();
            }
            catch (Exception ex)
            {
                Debugging.Error($"Gem {hooked.Name} threw in Stop: {ex}");

                continue;
            }

            _hookDuration.RecordSince(
                start,
                new(Metrics.GemNameTag, hooked.Name),
                new(Metrics.FramePhaseTag, nameof(IGem.Stop)));
        }
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
                Debugging.Error($"Gem {gem.GetType().Name} threw while disposing: {ex}");
            }
        }
    }

    /// <summary>
    /// Runs FixedUpdate for every fixed step up to <paramref name="frame"/>'s time, each given the step and the time it
    /// reaches. Past <see cref="MaxFixedSteps"/>, the steps left are dropped.
    /// </summary>
    private void RunFixedSteps(in Frame frame)
    {
        for (int step = 0; _fixedSeconds + FixedStep <= frame.Seconds; step++)
        {
            if (step == MaxFixedSteps)
            {
                _fixedSeconds = frame.Seconds;

                break;
            }

            _fixedSeconds += FixedStep;
            RunPhase(Phase.FixedUpdate, frame with { DeltaSeconds = (float)FixedStep, Seconds = _fixedSeconds });
        }
    }

    /// <summary>
    /// The frame handed over, on the render thread.
    /// </summary>
    private void RenderFrame()
    {
        try
        {
            RunPhase(Phase.Render, _rendering);
        }
        finally
        {
            _rendered.Set();
        }
    }

    private void WaitForRender()
    {
        long start = _renderWait.Start();
        _rendered.Wait();
        _renderWait.RecordSince(start);
    }

    /// <summary>
    /// Calls the hook for <paramref name="phase"/> on every gem that implements it and is still in the loop, timing
    /// each while something listens. A gem that throws is logged and leaves the frame loop for good, on both threads;
    /// the frame goes on without it.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever a gem throws, it is logged and the gem leaves the loop; the engine goes on.")]
    private void RunPhase(Phase phase, in Frame frame)
    {
        foreach (Hooked hooked in _hooked[(int)phase])
        {
            if (hooked.HasLeft) continue;

            long start = _hookDuration.Start();
            try
            {
                Call(hooked.Gem, phase, frame);
            }
            catch (Exception ex)
            {
                Debugging.Error($"Gem {hooked.Name} threw in {_phases[(int)phase]} and left the frame loop: {ex}");
                hooked.HasLeft = true;

                continue;
            }

            // Outside the try: a listener that throws is its own failure, not this gem's.
            _hookDuration.RecordSince(
                start,
                new(Metrics.GemNameTag, hooked.Name),
                new(Metrics.FramePhaseTag, _phases[(int)phase]));
        }
    }

    /// <summary>
    /// A gem in the frame loop, by the name the project lists it as, which its assembly is named for, what is
    /// registered for it, and whether it threw and left the loop for good: written and read on both threads.
    /// </summary>
    private sealed class Hooked(IGem gem, string name, SubscriptionList registrations)
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
