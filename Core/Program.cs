using CommandLine;
using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.Events;
using Magic.Contexts.Rendering;
using Magic.Interfaces;
using Magic.Services;
using Magic.Systems.DebugUI;
using Magic.Systems.Rendering;
using Magic.Systems.Streaming;
using Magic.Utils;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Magic;

/// <summary>
/// The host: parses the command line, loads the project and its gems, opens the main window and runs the frame loop.
/// Each frame builds one <see cref="Frame"/> and hands it down, phase by phase, to every gem; the ECS gem runs the
/// systems added to the <see cref="Scheduler"/>, the Core ones among them (see <see cref="Step"/>). Exit codes: 0 clean, 1 bad arguments, no project, no window or a crash, 2 errors were logged
/// and <c>--fail-on-error</c> asked.
/// </summary>
internal static class Program
{
    /// <summary>FixedUpdate runs at this constant step, so simulation stays deterministic whatever the frame rate.</summary>
    private const float FixedDelta = 1f / 60f;

    /// <summary>After a stall (breakpoint, window drag) do not try to catch up forever.</summary>
    private const double MaxDelta = 0.25;

    private static long _lastSlowLog;

    private static int Main(string[] args)
    {
        using Parser parser = new(with =>
        {
            with.HelpWriter = Console.Error;
            with.CaseInsensitiveEnumValues = true;
        });

        return parser.ParseArguments<Options>(args).MapResult(Run, errors => errors.IsHelp() || errors.IsVersion() ? 0 : 1);
    }

    private static int Run(Options options)
    {
        if (options.Validate() is { } problem)
        {
            Console.Error.WriteLine(problem);
            return 1;
        }

        Debugging.MinimumLevel = options.LogLevel;
        Debugging.Verbose = options.Verbose;

        try
        {
            using CancellationTokenSource shutdown = ListenForCtrlC();
            IFileSystem files = new FileSystem();

            Result<Project> loaded = LoadProject(options, files, FindEngineRoot(files));
            if (loaded.Failed)
                return Fail(loaded.Message);

            // The host's services, which gem constructors ask for by type; gems add theirs as they load.
            Container container = new();
            Events events = new();
            Scheduler scheduler = new();
            World world = new(events);
            using Assets assets = new(loaded.Payload, files, events, container); // indexes Resources and the project's Assets
            Project project = loaded.Payload.Icon.IsValid
                ? loaded.Payload
                : loaded.Payload with { Icon = assets.Find<Texture>("Icons/Mage.svg") }; // the engine's own icon unless the project names one

            container.Add(project);
            container.Add(files);
            container.Add(events);
            container.Add(assets);
            container.Add(scheduler);
            container.Add(world);
            Debugging.Log.Info($"Project {project.Name}: root {project.Root}; assets {project.Assets}; cache {Project.Cache}; resources {project.Resources}; engine gems {project.EngineGems}; gems [{string.Join(", ", project.Gems)}].");

            using Gems gems = new(container, files, events);
            gems.Load(project.EngineGems, project.Gems, project.Root);

            using CoreSystems? core = CreateSystems(container.Get<IEcs>(), container.Get<IInput>(), container.Get<IWindowRegistry>(), scheduler, assets, files, project, container); // disposed before the gems go

            if (!TryOpenMainWindow(options, container.Get<IWindowFactory>(), project, out IWindow? mainWindow))
                return 1;
            using IWindow? _ = mainWindow;

            OpenDomain(options, project, assets, world);

            MainLoop(options, new Engine(container, files, project, events, assets, gems, scheduler, core, mainWindow, new RenderCommands()), shutdown.Token);

            return ExitCode(options);
        }
        catch (Exception ex)
        {
            Fail($"Exception occurred, crashing...\nException:\n{ex}");
            return 1;
        }
    }

    /// <summary>
    /// The host's own systems, once the gems are loaded, added to the scheduler in the order they run within a phase.
    /// They need the ECS gem; without one there are none. The ECS, input and windowing gems are static, so the systems
    /// keep what they are given here (null when no gem provides it); the renderer lives in a reloadable gem, so
    /// <see cref="Step"/> hands the render system the loaded one every frame.
    /// </summary>
    private static CoreSystems? CreateSystems(IEcs? ecs, IInput? input, IWindowRegistry? windows, Scheduler scheduler, Assets assets, IFileSystem files, Project project, Container container)
    {
        if (ecs is null)
        {
            Debugging.Log.Warn("No loaded gem provides IEcs: nothing will be streamed, transformed or rendered.");
            return null;
        }

        ScriptSystem scripts = new(ecs, assets, scheduler, container); // its later phases' hooks go on the schedule here, ahead of transforms and rendering
        StreamingSystem streaming = new(ecs, assets, project, scripts);
        TransformSystem transforms = new(ecs);
        RenderSystem rendering = new(ecs, assets, files, project, windows);
        ConsoleSystem console = new(input);
        SettingsSystem settings = new(project.Settings, input);
        DebuggerDisplaySystem debugger = new(transforms, rendering, streaming, assets, input);

        ISystem[] systems = [console, settings, streaming, scripts, debugger, transforms, rendering];

        return new CoreSystems(console, settings, streaming, scripts, transforms, rendering, debugger, [.. systems.Select(system => scheduler.Add(ecs, system))]);
    }

    /// <summary>Opens the domain to start in: --domain, else the project's. It is spawned in the first frame.</summary>
    private static void OpenDomain(Options options, Project project, Assets assets, World world)
    {
        Handle<Domain> domain = options.Domain is { } path ? assets.Find<Domain>(path) : project.Domain;
        if (options.Domain is not null && !domain.IsValid)
            Debugging.Log.Error($"--domain {options.Domain}: no such asset under Resources or Assets.");
        else if (domain.IsValid)
            world.Open(domain);
        else
            Debugging.Log.Info("No domain to open: set \"domain\" in the .magic file or pass --domain.");
    }

    /// <summary>Ctrl+C ends the loop cleanly instead of killing the process mid-frame (gems get their Dispose, logs flush).</summary>
    private static CancellationTokenSource ListenForCtrlC()
    {
        CancellationTokenSource shutdown = new();
        Console.CancelKeyPress += (_, cancel) =>
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

            root = files.Parent(path)!;
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

        Stopwatch clock = Stopwatch.StartNew();
        double last = 0;
        double accumulator = 0;
        double worstMs = 0;
        double firstFrameMs = 0;
        long number = 0;
        int screenshotsLeft = options.Screenshots;
        long nextScreenshot = options.ScreenshotDelay;
        bool quit = false;
        using IDisposable quitting = engine.Events.Watch(EventType.Quit, _ => quit = true);

        while (!shutdown.IsCancellationRequested && !quit && (engine.MainWindow?.IsOpen ?? true))
        {
            number++;
            double now = clock.Elapsed.TotalSeconds;
            double delta = Math.Min(now - last, MaxDelta);
            last = now;
            accumulator += delta;
            worstMs = Math.Max(worstMs, delta * 1000d);

            long stepStarted = Stopwatch.GetTimestamp();
            Step(engine, number, now, (float)delta, ref accumulator);
            if (number == 1)
                firstFrameMs = Stopwatch.GetElapsedTime(stepStarted).TotalMilliseconds;

            // End of frame: whatever the renderer presented this frame is what a screenshot shows.
            if (screenshotsLeft > 0 && number >= nextScreenshot && engine.MainWindow is not null)
            {
                Screenshot(engine.Core?.Rendering, engine.Files, engine.Project, engine.MainWindow.Handle, number);
                screenshotsLeft--;
                nextScreenshot += options.ScreenshotInterval;
            }

            if (options.Lifetime > 0 && number >= options.Lifetime)
            {
                Debugging.Log.Info($"Lifetime of {options.Lifetime} frames reached.");
                break;
            }
        }

        LogSummary(number, clock.Elapsed.TotalMilliseconds, firstFrameMs, worstMs, shutdown.IsCancellationRequested);
    }

    /// <summary>
    /// One frame, top to bottom. Gem and asset changes land first and are published; then one <see cref="Frame"/>
    /// carrying every event since the last frame goes down: each phase calls every gem in load order, and the ECS
    /// gem's hook runs the phase's systems, which the scheduler hands that frame.
    /// </summary>
    private static void Step(Engine engine, long number, double time, float delta, ref double accumulator)
    {
        long started = Stopwatch.GetTimestamp();

        engine.Gems.ProcessChanges();
        engine.Assets.ProcessChanges();
        Frame frame = new(number, time, delta, engine.Events.NextFrame(), engine.Commands);
        IGem[] gems = engine.Gems.Loaded;
        CoreSystems? core = engine.Core;
        engine.Scheduler.SetFrame(frame);

        // Gems only change in ProcessChanges, so the renderer loaded now is the frame's.
        IRendering? rendering = engine.Container.Get<IRendering>();
        if (core is not null)
            core.Rendering.Renderer = rendering;
        long afterChanges = Stopwatch.GetTimestamp();

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
        // later gem draws in its Render hook lands on top of it; then it all goes.
        foreach (IGem gem in gems)
            gem.Render(frame);

        // Without a renderer nothing runs the commands, and without the Core systems nothing recorded a scene, but a gem
        // may still have drawn: its commands still go.
        long submitting = Stopwatch.GetTimestamp();
        float waitMs = 0f;
        if (rendering is not null)
            waitMs = rendering.Submit(frame.Commands);
        else
            frame.Commands.Clear();
        long finished = Stopwatch.GetTimestamp();

        core?.Rendering.Finish(frame, (float)Ms(submitting, finished), waitMs);

        // A slow frame says where it went, at most every few seconds.
        double totalMs = Stopwatch.GetElapsedTime(started, finished).TotalMilliseconds;
        if (totalMs <= 50 || Environment.TickCount64 - _lastSlowLog <= 5000)
            return;

        _lastSlowLog = Environment.TickCount64;
        Debugging.Log.Verbose($"Slow frame ({totalMs:F1} ms): changes {Ms(started, afterChanges):F1}, update {Ms(afterChanges, afterUpdate):F1}, fixed {Ms(afterUpdate, afterFixed):F1} ({fixedSteps} steps), late {Ms(afterFixed, afterLate):F1}, render {Ms(afterLate, finished):F1}.");

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

        if (stoppedByCtrlC)
            Debugging.Log.Info("Stopped by Ctrl+C.");

        if (Debugging.Errors > 0)
            Debugging.Log.Warn($"{Debugging.Errors} message(s) were logged at Error or above this run.");
    }

    private static int ExitCode(Options options)
    {
        return options.FailOnError && Debugging.Errors > 0 ? 2 : 0;
    }

    /// <summary>An error before any logger gem is up would only sit in the log queue; say it on stderr as well. Always 1, the exit code.</summary>
    private static int Fail(string message)
    {
        Debugging.Assert(false, message);
        return 1;
    }

    /// <summary>Captures the window's last frame and writes it to <see cref="Project.Screenshots"/>. Every failure is logged as an error: a screenshot that was asked for and not taken is a failed run.</summary>
    private static void Screenshot(RenderSystem? rendering, IFileSystem files, Project project, uint window, long frame)
    {
        if (rendering is null)
        {
            Debugging.Log.Error($"Screenshot at frame {frame} skipped: nothing is rendered without an ECS.");
            return;
        }

        Result<CapturedFrame> captured = rendering.Capture(RenderTarget.Of(window));
        if (captured.Failed)
        {
            Debugging.Log.Error($"Screenshot at frame {frame} failed: {captured.Message}");
            return;
        }

        string path = files.Combine(Project.Screenshots, $"{project.Name}_{frame:D6}.png");
        byte[] png = Png.Encode(captured.Payload.Width, captured.Payload.Height, captured.Payload.Pixels);
        Result written = files.WriteBinary(path, png);
        if (written.Failed)
        {
            Debugging.Log.Error($"Screenshot at frame {frame} could not be written to {path}: {written.Message}");
            return;
        }

        Debugging.Log.Info($"Screenshot at frame {frame}: {path} ({captured.Payload.Width}x{captured.Payload.Height}).");
    }

    /// <summary>Everything a frame needs, built once by <see cref="Run"/> and disposed with it.</summary>
    private readonly record struct Engine(
        Container Container,
        IFileSystem Files,
        Project Project,
        Events Events,
        Assets Assets,
        Gems Gems,
        Scheduler Scheduler,
        CoreSystems? Core,
        IWindow? MainWindow,
        RenderCommands Commands);

    /// <summary>The host's own systems and their places on the schedule, which go first; then the systems, newest first.</summary>
    private sealed record CoreSystems(
        ConsoleSystem Console,
        SettingsSystem Settings,
        StreamingSystem Streaming,
        ScriptSystem Scripts,
        TransformSystem Transforms,
        RenderSystem Rendering,
        DebuggerDisplaySystem Debugger,
        IDisposable[] Scheduled) : IDisposable
    {
        public void Dispose()
        {
            foreach (IDisposable scheduled in Scheduled)
                scheduled.Dispose();

            Rendering.Dispose();
            Transforms.Dispose();
            Scripts.Dispose(); // before streaming destroys the entities under them
            Streaming.Dispose();
            Debugger.Dispose();
            Settings.Dispose();
            Console.Dispose();
        }
    }
}
