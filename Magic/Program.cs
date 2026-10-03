using CommandLine;
using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Events;
using Magic.Contexts.Rendering;
using Magic.Interfaces;
using Magic.Services;
using Magic.Systems;
using Magic.Utils;
using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Magic;

/// <summary>
/// The host: parses the command line, loads the project and its gems, opens the main window and runs the frame loop.
/// Each frame builds one <see cref="Frame"/> and hands it down, phase by phase, to every gem; the ECS gem runs the
/// systems added to the <see cref="Scheduler"/>, the Core ones among them (see <see cref="Step"/>). Two threads
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

    /// <summary>
    /// The last frame's submit, running on the render thread; the next frame's drawing queues behind it.
    /// </summary>
    private static Task _submitted = Task.CompletedTask;

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

        Debugging.Log.MinimumLevel = options.LogLevel;
        Debugging.Log.EnableVerbose = options.Verbose;

        // A game would rather have the heap grow than have every thread stopped for a full collection mid-frame.
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        try
        {
            using CancellationTokenSource shutdown = ListenForCtrlC();

            IFileSystem files = new FileSystem();
            Result<Project> loaded = LoadProject(options, files, FindEngineRoot(files));
            if (loaded.Failed)
                return Fail(loaded.Message);

            // The host's services, which gem constructors ask for by type; gems add theirs as they load.
            Container services = CoreServices.Create(loaded.Payload, files);
            using Threads threads = services.Get<Threads>();
            threads.Claim(ThreadId.Render); // this thread, the one the process started on, is the render thread: work for it waits for RunUntil below
            using Assets assets = services.Get<Assets>();
            Events events = services.Get<Events>();
            Project project = services.Get<Project>();

            Debugging.Log.Info($"Project {project.Name}: root {project.Root}; assets {project.Assets}; cache {project.Cache}; resources {project.Resources}; engine gems {project.EngineGems}; gems [{string.Join(", ", project.Gems)}].");

            using Gems gems = new(services, files, events, threads);
            gems.Load(project.EngineGems, project.Gems, project.Root);

            using CoreSystems? systems = CoreSystems.Create(services); // disposed before the gems go

            // What a gem provides may not be there: the window factory, the renderer.
            services.TryGet(out IWindowFactory? windowFactory);
            services.TryGet(out IRendering? rendering);
            services.TryGet(out IEcs? ecs);

            if (!TryOpenMainWindow(options, windowFactory, project, out IWindow? mainWindow))
                return 1;
            using IWindow? _ = mainWindow;

            World world = services.Get<World>();
            OpenEntryPoint(options, project, assets, world);

            // The frame loop runs on the main thread while this one draws what it is handed, until the loop ends.
            Engine engine = new(
                files,
                project,
                events,
                assets,
                world,
                ecs,
                gems,
                services.Get<Scheduler>(),
                threads,
                rendering,
                mainWindow,
                new RenderCommands());
            Task loop = threads.StartAsync(ThreadId.Main, () =>
            {
                MainLoop(options, engine, shutdown.Token);
                _submitted.Wait(); // the last frame is on screen before anything is torn down
            });
            threads.RunUntil(ThreadId.Render, loop);
            loop.GetAwaiter().GetResult(); // what the loop threw is thrown here

            return options.FailOnError && Debugging.Log.Errors > 0 ? 2 : 0;
        }
        catch (Exception ex)
        {
            Fail($"Exception occurred, crashing...\nException:\n{ex}");
            return 1;
        }
    }

    /// <summary>
    /// Opens the domain to start in: --entry-point, else the project's entry point. It streams in from the first
    /// frame on, behind the loading domain: --loading, else the project's.
    /// </summary>
    private static void OpenEntryPoint(Options options, Project project, Assets assets, World world)
    {
        if (options.Loading is { } loading)
        {
            world.Loading = assets.Find<Domain>(loading);
            if (!world.Loading.IsValid)
                Debugging.Log.Error($"--loading {loading}: no such asset under Resources or Assets.");
        }

        Handle<Domain> entryPoint = options.EntryPoint is { } path ? assets.Find<Domain>(path) : project.EntryPoint;
        if (options.EntryPoint is not null && !entryPoint.IsValid)
            Debugging.Log.Error($"--entry-point {options.EntryPoint}: no such asset under Resources or Assets.");
        else if (!entryPoint.IsValid)
            Debugging.Log.Info("No entry point to open: set \"entryPoint\" in the .magic file or pass --entry-point.");
        else if (world.Open(entryPoint) is { Failed: true } opened)
            Debugging.Log.Error(opened.Message);
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
    /// The engine's own root, where Resources live: the repo in a Debug or Optimize build (stamped into the
    /// assembly as MagicRoot by Magic.csproj), the exe folder in Release, where the build copied them.
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
        JsonNodeOptions nodeOptions = new() { PropertyNameCaseInsensitive = true };
        JsonObject json = new(nodeOptions);
        string root = engineRoot;

        if (options.Project is not null)
        {
            string path = files.FullPath(options.Project);
            if (files.DirectoryExists(path))
                path = files.ReadDirectory(path, "*.magic").Payload?.FirstOrDefault() ?? files.Combine(path, "*.magic");

            Result<string> read = files.ReadText(path);
            if (read.Failed)
                return Result<Project>.Failure($"Could not read project {path}: {read.Message}");

            JsonDocumentOptions documentOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            try
            {
                json = JsonNode.Parse(read.Payload, nodeOptions, documentOptions) as JsonObject ?? json;
            }
            catch (JsonException ex)
            {
                return Result<Project>.Failure($"Project {path} is not valid JSON: {ex.Message}");
            }

            root = files.Parent(path) ?? root;
            Debugging.Log.Info($"Project file: {path}");
        }

        if (options.Root is not null)
            root = files.FullPath(options.Root);

        // --set Render.Vsync=false writes settings.render.vsync; a value that isn't JSON is a string, which is what an enum wants.
        foreach (string setting in options.Set)
        {
            string[] pair = setting.Split('=', 2);
            if (pair.Length < 2 || string.IsNullOrWhiteSpace(pair[0]))
                continue;

            string[] path = ["settings", .. pair[0].Split('.', StringSplitOptions.TrimEntries)];
            JsonObject node = json;
            foreach (string key in path[..^1])
                node = node[key] as JsonObject ?? (JsonObject)(node[key] = new JsonObject(nodeOptions));

            try
            {
                node[path[^1]] = JsonNode.Parse(pair[1], nodeOptions);
            }
            catch (JsonException)
            {
                node[path[^1]] = pair[1];
            }
        }

        Project project;
        try
        {
            project = json.Deserialize<Project>(AssetJson.Options) ?? new Project();
        }
        catch (JsonException ex)
        {
            return Result<Project>.Failure($"Project settings are not valid: {ex.Message}");
        }

        return Result<Project>.Success(project with
        {
            Root = root,
            EngineGems = files.FullPath(options.Gems ?? files.Combine(AppContext.BaseDirectory, "Gems")),
            Resources = files.Combine(engineRoot, "Resources"),
        });
    }

    /// <summary>
    /// Opens the main window, unless headless: then gems and systems run with no window at all.
    /// False when a window was wanted and no loaded gem can provide one.
    /// </summary>
    private static bool TryOpenMainWindow(Options options, IWindowFactory? factory, Project project, out IWindow? window)
    {
        window = null;

        if (options.Headless)
            return true;

        if (factory is null)
        {
            Debugging.Log.Error($"No loaded gem provides {nameof(IWindowFactory)}, so the main window cannot be opened. Is the SDL gem in {project.EngineGems} and listed in the project's gems? Pass --headless to run without one.");
            return false;
        }

        window = factory.Create(project.Name, options.Width, options.Height, options.WindowMode);
        window.Show();

        return true;
    }

    /// <summary>
    /// Fixed timestep with an accumulator: Update and LateUpdate get the real frame time, FixedUpdate runs
    /// zero or more times per frame at a constant step. Runs until the window closes, the world ends, Ctrl+C or
    /// --lifetime.
    /// </summary>
    private static void MainLoop(Options options, Engine engine, CancellationToken shutdown)
    {
        if (options.Lifetime > 0 && options.LastScreenshotFrame > options.Lifetime)
            Debugging.Log.Warn($"The last screenshot is due at frame {options.LastScreenshotFrame}, after the lifetime of {options.Lifetime} frames; it will not be taken.");

        string headless = engine.MainWindow is null ? " (headless)" : "";
        string lifetime = options.Lifetime > 0 ? $" for {options.Lifetime} frames" : "";
        Debugging.Log.Info($"Entering the main loop on thread {Environment.CurrentManagedThreadId}{headless}{lifetime}.");

        bool quit = false;
        using IDisposable quitting = engine.Events.Watch(EventType.Quit, _ => quit = true);

        Stopwatch clock = Stopwatch.StartNew();
        long frameIndex = 0;
        double lastFrameTimeSeconds = 0;
        double fixedUpdateAccumulatorSeconds = 0;
        double worstFrameMs = 0;
        double firstFrameMs = 0;

        int screenshotsLeft = options.Screenshots;
        long screenshotDelay = options.ScreenshotDelay;

        while (!shutdown.IsCancellationRequested && !quit && (engine.MainWindow?.IsOpen ?? true))
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

            // End of frame: whatever the renderer presented this frame is what a screenshot shows.
            if (screenshotsLeft > 0 && frameIndex >= screenshotDelay && engine.MainWindow is not null)
            {
                long frameNumber = frameIndex;
                engine.Threads.Invoke(ThreadId.Render, () => Screenshot(engine, frameNumber));
                screenshotsLeft--;
                screenshotDelay += options.ScreenshotInterval;
            }

            if (options.Lifetime > 0 && frameIndex >= options.Lifetime)
            {
                Debugging.Log.Info($"Lifetime of {options.Lifetime} frames reached.");
                break;
            }
        }

        LogSummary(frameIndex, clock.Elapsed.TotalMilliseconds, firstFrameMs, worstFrameMs, shutdown.IsCancellationRequested);
    }

    /// <summary>
    /// One frame, top to bottom, on the main thread. What other threads posted to it runs first; gem and asset
    /// changes land and are published; then one <see cref="Frame"/> carrying every event since the last frame goes
    /// down: each phase calls every gem in load order, and the ECS gem's hook runs the phase's systems, which the
    /// scheduler hands that frame. The Render phase runs on the render thread while this one waits for it; the
    /// submit that follows runs there too, and is not waited for: the next frame begins at once, and only its own
    /// Render phase queues behind it, so the simulation is never more than a frame ahead of what is shown.
    /// </summary>
    private static void Step(Engine engine, long number, double time, float delta, ref double accumulator)
    {
        long started = Stopwatch.GetTimestamp();

        Threads threads = engine.Threads;
        threads.RunPending(ThreadId.Main);

        engine.Gems.ProcessChanges();
        engine.Assets.ProcessChanges();

        long afterChanges = Stopwatch.GetTimestamp();

        Frame frame = new(number, time, delta, engine.Events.NextFrame(), engine.DrawCommands);
        engine.Scheduler.SetFrame(frame);

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

        // The ECS gem loads before anything that draws, so the render system records the scene first and whatever a
        // later gem draws in its Render hook lands on top of it. A submit that threw is thrown here, a frame late.
        if (_submitted.IsFaulted)
            _submitted.GetAwaiter().GetResult();

        threads.Invoke(ThreadId.Render, () =>
        {
            foreach (IGem gem in gems)
                gem.Render(frame);
        });

        // Without a renderer nothing runs the commands, and without the Core systems nothing recorded a scene, but a gem
        // may still have drawn: its commands still go. How long it took stays on the list, for the next frame's stats.
        RenderCommands commands = frame.DrawCommands;
        IRendering? rendering = engine.Rendering;
        _submitted = threads.InvokeAsync(ThreadId.Render, _ =>
        {
            long submitting = Stopwatch.GetTimestamp();
            if (rendering is not null)
                commands.WaitMs = rendering.Submit(commands);
            else
                commands.Clear();

            commands.SubmitMs = (float)Stopwatch.GetElapsedTime(submitting).TotalMilliseconds;
        });

        long finished = Stopwatch.GetTimestamp();

        // A slow frame says where it went, at most every few seconds.
        double totalMs = Stopwatch.GetElapsedTime(started, finished).TotalMilliseconds;
        if (totalMs <= 50 || Environment.TickCount64 - _lastSlowLog <= 5000)
            return;

        _lastSlowLog = Environment.TickCount64;
        Debugging.Log.Verbose($"Slow frame ({totalMs:F1} ms): changes {Ms(started, afterChanges):F1}, update {Ms(afterChanges, afterUpdate):F1}, fixed {Ms(afterUpdate, afterFixed):F1} ({fixedSteps} steps), late {Ms(afterFixed, afterLate):F1}, render {Ms(afterLate, finished):F1} (with the wait for the frame before to be submitted).");

        static double Ms(long from, long to)
        {
            return Stopwatch.GetElapsedTime(from, to).TotalMilliseconds;
        }
    }

    private static void LogSummary(long frames, double totalMs, double firstFrameMs, double worstFrameMs, bool stoppedByCtrlC)
    {
        // The first frame loads the scene; the average that matters is the one after it.
        double restMs = frames > 1 ? (totalMs - firstFrameMs) / (frames - 1) : totalMs;
        Debugging.Log.Info($"Ran {frames} frames in {totalMs:F0} ms: first {firstFrameMs:F0} ms, then {restMs:F2} ms average, {worstFrameMs:F2} ms worst.");

        // What the collector cost: every pause is a frame that waited.
        Debugging.Log.Info($"Garbage collection: {GC.GetTotalAllocatedBytes() / (1024d * 1024d):F0} MB allocated, {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} collections (gen 0/1/2), {GC.GetTotalPauseDuration().TotalMilliseconds:F0} ms paused.");

        if (stoppedByCtrlC)
            Debugging.Log.Info("Stopped by Ctrl+C.");

        if (Debugging.Log.Errors > 0)
            Debugging.Log.Warn($"{Debugging.Log.Errors} message(s) were logged at Error or above this run.");
    }

    /// <summary>
    /// An error before any logger gem is up would only sit in the log queue; say it on stderr as well. Always 1, the exit code.
    /// </summary>
    private static int Fail(string message)
    {
        Debugging.Assert(false, message);
        return 1;
    }

    /// <summary>
    /// Writes what the window last showed, with the world's state in it, to <see cref="Project.Screenshots"/>. Every failure is logged as an error: a screenshot that was asked for and not taken is a failed run.
    /// </summary>
    private static void Screenshot(Engine engine, long frame)
    {
        if (engine.Rendering is null)
        {
            Debugging.Log.Error($"Screenshot at frame {frame} skipped: no loaded gem provides {nameof(IRendering)}.");
            return;
        }

        if (engine.MainWindow is null)
        {
            Debugging.Log.Error($"Screenshot at frame {frame} skipped: no loaded gem provides {nameof(IWindow)} or we are headless.");
            return;
        }

        string path = engine.Files.Combine(Project.Screenshots, $"{engine.Project.Name}_{frame:D6}.png");
        Result taken = Debugging.Screenshot.Capture(engine.Files, engine.Rendering, engine.MainWindow, engine.World, engine.Ecs, path);
        if (taken.Failed)
        {
            Debugging.Log.Error($"Screenshot at frame {frame} failed: {taken.Message}");
            return;
        }

        Debugging.Log.Info($"Screenshot at frame {frame}: {path}.");
    }

    /// <summary>
    /// Everything a frame needs, built once by <see cref="Run"/> and disposed with it.
    /// </summary>
    private readonly record struct Engine(
        IFileSystem Files,
        Project Project,
        Events Events,
        Assets Assets,
        World World,
        IEcs? Ecs,
        Gems Gems,
        Scheduler Scheduler,
        Threads Threads,
        IRendering? Rendering,
        IWindow? MainWindow,
        RenderCommands DrawCommands);
}
