using CommandLine;
using Magic.Contexts;
using Magic.Contexts.Events;
using Magic.Contexts.Settings;
using Magic.Contexts.Threading;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;
using Magic.Utils;
using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Runtime.CompilerServices;

namespace Magic;

/// <summary>
/// The host: parses the command line, loads the project and its gems, opens the main window and runs the frame loop.
/// Each frame builds one <see cref="Frame"/> and hands it down, phase by phase, to every gem; the ECS gem runs the
/// systems added to it, the engine's own among them (see <see cref="Step"/>). Two threads
/// share the work: the one the process starts on owns the windows and the GPU, as the OS wants, so it is the render
/// thread (<see cref="ThreadId.Render"/>); the frame loop runs on a main thread started beside it
/// (<see cref="ThreadId.Main"/>) and hands it each frame's Render hooks and the submit. Start-up and shutdown happen
/// on the first, alone. Exit codes: 0 clean, 1 bad arguments, no project, no window or a crash, 2 errors were logged
/// and <c>--fail-on-error</c> asked.
/// </summary>
internal static class Program
{
    /// <summary>
    /// FixedUpdate runs at this constant step, so simulation stays deterministic whatever the frame rate.
    /// </summary>
    private const float FixedDelta = 1f / 60f;

    /// <summary>
    /// After a stall (breakpoint, window drag) do not try to catch up forever.
    /// </summary>
    private const double MaxDelta = 0.25;

    private static long _lastSlowLog;

    private static int Main(string[] args)
    {
        using Parser parser = new(with =>
        {
#pragma warning disable RS0030 // the command line's own help, before any logger is up
            with.HelpWriter = Console.Error;
#pragma warning restore RS0030
            with.CaseInsensitiveEnumValues = true;
        });

        return parser.ParseArguments<Options>(args).MapResult(Run, errors => errors.IsHelp() || errors.IsVersion() ? 0 : 1);
    }

    private static int Run(Options options)
    {
        if (options.Validate() is { } problem)
            return Fail(problem);

        Log.MinimumLevel = options.LogLevel;
        Log.EnableVerbose = options.Verbose;

        // A game would rather have the heap grow than have every thread stopped for a full collection mid-frame.
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        try
        {
            using CancellationTokenSource shutdown = ListenForCtrlC();

            IFileSystem files = new FileSystem();
            Result<Project> loaded = LoadProject(options, files, FindEngineRoot(files));
            if (loaded.Failed)
                return Fail(loaded.Message);

            Project project = loaded.Payload;
            Events events = new();
            using Threads threads = new();
            threads.Claim(ThreadId.Render); // this thread, the one the process started on, is the render thread: work for it waits for RunUntil below

            // The host's services, which gem constructors ask for by type; gems add theirs as they load.
            Settings settings = new(options.SettingLines);
            Telemetry telemetry = new();
            GemResources resources = new();
            if (Gems.ResourcesOf(typeof(Program).Assembly) is { } own)
                resources.Add(own); // the engine's own assets, there for the whole run
            Container services = new();
            services.Add<IServices>(services); // the read side, for whoever must look services up by type
            services.Add(project);
            services.Add(files);
            services.Add(events);
            services.Add(threads);
            services.Add(new MainThread(threads));
            services.Add(telemetry);
            services.Add<ITelemetry>(telemetry);
            services.Add(settings);
            services.Add<ITypeRegistrar>(settings); // gems' [Settings] classes, as each gem loads
            services.Add(resources);
            Gems.RegisterHost(services, settings); // the engine's own [Settings] classes, which its services take

            services.Add(new Randomness(services.Get<RandomSettings>()));
            DebugUI debug = new();
            services.Add(debug);
            services.Add<IDebugUI>(debug); // what a gem shows the developer
            services.Add<IConsoleCommands>(debug);

            // Assets indexes the engine's and every gem's Resources as they come, so it is made before any gem loads, and it
            // outlives every gem: it is disposed after them.
            using Assets assets = new(project, files, services.Get<AssetSettings>(), events, services, threads, telemetry, resources);
            services.Add(assets);
            services.Add<ITypeRegistrar>(assets); // gems' asset types, as each gem loads
            Gems.RegisterHost(services, assets); // the engine's own asset types
            World world = new(events, assets, threads);
            services.Add(world);

            Log.Info($"Project {project.Name}: root {project.Root}; assets {project.Assets}; cache {project.Cache}; engine gems {project.EngineGems}; gems [{string.Join(", ", project.Gems)}].");

            using Gems gems = new(services, files, events, threads, resources);
            gems.Load(project.EngineGems, project.Gems, project.Root);
            world.OpenEntryPoint(project, services.Get<WorldSettings>());

            // The frame loop runs on the main thread while this one draws what it is handed, until the loop ends.
            Engine engine = new(events, gems, assets, threads);
            Task loop = threads.StartAsync(ThreadId.Main, () =>
            {
                MainLoop(options, engine, shutdown.Token);
            });
            threads.RunUntil(ThreadId.Render, loop);
            loop.GetAwaiter().GetResult(); // what the loop threw is thrown here

            return options.FailOnError && Log.Errors > 0 ? 2 : 0;
        }
        catch (Exception ex)
        {
            Fail($"Exception occurred, crashing...\nException:\n{ex}");
            return 1;
        }
    }

    /// <summary>
    /// Ctrl+C ends the loop cleanly instead of killing the process mid-frame (gems get their Dispose, logs flush).
    /// </summary>
    private static CancellationTokenSource ListenForCtrlC()
    {
        CancellationTokenSource shutdown = new();
#pragma warning disable RS0030 // Ctrl+C is the console's to report
        Console.CancelKeyPress += (_, cancel) =>
#pragma warning restore RS0030
        {
            cancel.Cancel = true;
            shutdown.Cancel();
        };

        return shutdown;
    }

    /// <summary>
    /// The engine's own root, the project a run without --project opens: the repo in a Debug or Optimize build
    /// (stamped into the assembly as MagicRoot by Core.csproj), the exe folder in a shipped one.
    /// </summary>
    private static string FindEngineRoot(IFileSystem files)
    {
        return Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "MagicRoot" && !string.IsNullOrEmpty(attribute.Value) && files.DirectoryExists(attribute.Value))?.Value
            ?? AppContext.BaseDirectory;
    }

    /// <summary>
    /// A project is a folder with a .magic file; that folder is the root unless --root says otherwise.
    /// Without --project the engine is its own project, so a plain "dotnet run" keeps working.
    /// The engine's gems sit next to the exe unless --gems says otherwise; the file picks which of them load.
    /// </summary>
    private static Result<Project> LoadProject(Options options, IFileSystem files, string engineRoot)
    {
        Project project = new();
        string root = engineRoot;

        if (options.Project is not null)
        {
            string path = files.FullPath(options.Project);
            if (files.DirectoryExists(path))
                path = files.ReadDirectory(path, "*.magic").Payload?.FirstOrDefault() ?? files.Combine(path, "*.magic");

            Result<byte[]> read = files.ReadBinary(path);
            if (read.Failed)
                return Result<Project>.Failure($"Could not read project {path}: {read.Message}");

            Result<Project> parsed = AssetJson.Read<Project>(read.Payload);
            if (parsed.Failed)
                return Result<Project>.Failure($"Project {path} is not valid: {parsed.Message}");

            project = parsed.Payload;
            root = files.Parent(path) ?? root;
            Log.Info($"Project file: {path}");
        }

        if (options.Root is not null)
            root = files.FullPath(options.Root);

        if (project.Sections.Remove("settings"))
            Log.Warn("The project file has \"settings\", which are no longer read there: settings live in a preset (\"preset\": { \"id\": N }).");

        return Result<Project>.Success(project with
        {
            Root = root,
            EngineGems = files.FullPath(options.Gems ?? files.Combine(AppContext.BaseDirectory, "Gems")),
        });
    }

    /// <summary>
    /// Fixed timestep with an accumulator: Update and LateUpdate get the real frame time, FixedUpdate runs
    /// zero or more times per frame at a constant step. Runs until something publishes <see cref="Quit"/> (the main window closed, the world ended), Ctrl+C or
    /// --lifetime.
    /// </summary>
    private static void MainLoop(Options options, Engine engine, CancellationToken shutdown)
    {
        string lifetime = options.Lifetime > 0 ? $" for {options.Lifetime} frames" : "";
        Log.Info($"Entering the main loop on thread {Environment.CurrentManagedThreadId}{lifetime}.");

        bool quit = false;
        using Subscription quitting = engine.Events.Watch<Quit>(_ => quit = true);

        Stopwatch clock = Stopwatch.StartNew();
        long frameIndex = 0;
        double lastFrameTimeSeconds = 0;
        double fixedUpdateAccumulatorSeconds = 0;
        double worstFrameMs = 0;
        double firstFrameMs = 0;

        while (!shutdown.IsCancellationRequested && !quit)
        {
            frameIndex++;
            double now = clock.Elapsed.TotalSeconds;
            double delta = Math.Min(now - lastFrameTimeSeconds, MaxDelta);
            lastFrameTimeSeconds = now;
            fixedUpdateAccumulatorSeconds += delta;
            worstFrameMs = Math.Max(worstFrameMs, delta * 1000d);

            long stepStarted = Stopwatch.GetTimestamp();
            Step(engine, frameIndex, now, (float)delta, ref fixedUpdateAccumulatorSeconds);
            if (frameIndex == 1)
                firstFrameMs = Stopwatch.GetElapsedTime(stepStarted).TotalMilliseconds;

            if (options.Lifetime > 0 && frameIndex >= options.Lifetime)
            {
                Log.Info($"Lifetime of {options.Lifetime} frames reached.");
                break;
            }
        }

        LogSummary(frameIndex, clock.Elapsed.TotalMilliseconds, firstFrameMs, worstFrameMs, shutdown.IsCancellationRequested);
    }

    /// <summary>
    /// One frame, top to bottom, on the main thread. What other threads posted to it runs first; gem and asset
    /// changes land and are published; then one <see cref="Frame"/> carrying every event since the last frame goes
    /// down: each phase calls every gem in load order, and the ECS gem's hook runs the phase's systems, which the
    /// scheduler hands that frame. Render and Present run on the render thread while this one waits for them; the
    /// renderer's submit queues there after them and is not waited for: the next frame begins at once, and only its own
    /// Render phase queues behind it, so the simulation is never more than a frame ahead of what is shown.
    /// </summary>
    private static void Step(Engine engine, long number, double time, float delta, ref double accumulator)
    {
        long started = Stopwatch.GetTimestamp();

        Threads threads = engine.Threads;
        threads.RunPending(ThreadId.Main);

        engine.Gems.ProcessChanges();

        long afterChanges = Stopwatch.GetTimestamp();

        engine.Events.NextFrame();
        engine.Assets.ProcessChanges(); // before any gem's Update, so each sees this frame's changes
        Frame frame = new(number, time, delta, engine.Events);

        IGem[] gems = engine.Gems.Loaded;
        foreach (IGem gem in gems)
            gem.Update(frame);

        long afterUpdate = Stopwatch.GetTimestamp();

        int fixedSteps = 0;
        for (; accumulator >= FixedDelta; accumulator -= FixedDelta, fixedSteps++)
        {
            Frame step = frame with { Delta = FixedDelta };
            foreach (IGem gem in gems)
                gem.FixedUpdate(step);
        }

        long afterFixed = Stopwatch.GetTimestamp();

        foreach (IGem gem in gems)
            gem.LateUpdate(frame);

        long afterLate = Stopwatch.GetTimestamp();

        // Render in load order, then Present the other way round: what depends on another draws on top of it.
        threads.Invoke(ThreadId.Render, () =>
        {
            foreach (IGem gem in gems)
                gem.Render(frame);

            for (int i = gems.Length - 1; i >= 0; i--)
                gems[i].Present(frame);
        });

        long finished = Stopwatch.GetTimestamp();

        // A slow frame says where it went, at most every few seconds.
        double totalMs = Stopwatch.GetElapsedTime(started, finished).TotalMilliseconds;
        if (totalMs <= 50 || Environment.TickCount64 - _lastSlowLog <= 5000)
            return;

        _lastSlowLog = Environment.TickCount64;
        Log.Verbose($"Slow frame ({totalMs:F1} ms): changes {Ms(started, afterChanges):F1}, update {Ms(afterChanges, afterUpdate):F1}, fixed {Ms(afterUpdate, afterFixed):F1} ({fixedSteps} steps), late {Ms(afterFixed, afterLate):F1}, render {Ms(afterLate, finished):F1} (with the wait for the frame before to be submitted).");

        static double Ms(long from, long to)
        {
            return Stopwatch.GetElapsedTime(from, to).TotalMilliseconds;
        }
    }

    private static void LogSummary(long frames, double totalMs, double firstFrameMs, double worstFrameMs, bool stoppedByCtrlC)
    {
        // The first frame loads the scene; the average that matters is the one after it.
        double restMs = frames > 1 ? (totalMs - firstFrameMs) / (frames - 1) : totalMs;
        Log.Info($"Ran {frames} frames in {totalMs:F0} ms: first {firstFrameMs:F0} ms, then {restMs:F2} ms average, {worstFrameMs:F2} ms worst.");

        // What the collector cost: every pause is a frame that waited.
        Log.Info($"Garbage collection: {GC.GetTotalAllocatedBytes() / (1024d * 1024d):F0} MB allocated, {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} collections (gen 0/1/2), {GC.GetTotalPauseDuration().TotalMilliseconds:F0} ms paused.");

        if (stoppedByCtrlC)
            Log.Info("Stopped by Ctrl+C.");

        if (Log.Errors > 0)
            Log.Warn($"{Log.Errors} message(s) were logged at Error or above this run.");
    }

    /// <summary>
    /// Logs <paramref name="message"/> as critical, which also writes it to stderr and the crash file when no logger gem
    /// is up yet. Always 1, the exit code.
    /// </summary>
    private static int Fail(string message, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        Log.Write(LogLevel.Critical, message, file, line);
        return 1;
    }

    /// <summary>
    /// What a frame needs, built once by <see cref="Run"/>, which also disposes it.
    /// </summary>
    private readonly record struct Engine(Events Events, Gems Gems, Assets Assets, Threads Threads);
}
