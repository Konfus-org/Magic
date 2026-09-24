using CommandLine;
using DryIoc;
using Magic.Contexts.Assets;
using Magic.Interfaces;
using Magic.Services;
using Magic.Utils;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace Magic;

/// <summary>
/// The host: parses the command line, builds the container, loads the project and its gems, opens the main
/// window and runs the frame loop. Exit codes: 0 clean, 1 bad arguments, no project, no window or a crash,
/// 2 errors were logged and <c>--fail-on-error</c> asked.
/// </summary>
internal static class Program
{
    // Fixed timestep: FixedUpdate runs at a constant step so simulation stays deterministic regardless of frame rate.
    // All times are in milliseconds.
    private const double FixedDeltaMs = 1000d / 60d;
    private const double MaxFrameMs = 250d; // after a stall (breakpoint, window drag) do not try to catch up forever

    /// <summary>Everything a frame needs, built once by <see cref="Run"/> and disposed with it.</summary>
    private readonly record struct Engine(
        Container Container,
        IFileSystem Files,
        Project Project,
        SystemScheduler Systems,
        AssetManager Assets,
        Gems Gems,
        IWindow? MainWindow);

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

        try
        {
            using CancellationTokenSource shutdown = ListenForCtrlC();
            using Container container = CreateContainer();
            IFileSystem files = container.Resolve<IFileSystem>();

            Result<Project> loaded = LoadProject(options, files, FindEngineRoot(files));
            if (loaded.Failed)
                return Fail(loaded.Message);
            Project project = loaded.Payload;
            container.RegisterInstance(project);
            Debugging.LogInfo($"Project {project.Name}: root {project.Root}; assets {project.Assets}; cache {project.Cache}; resources {project.Resources}; gems {project.Gems}.");

            SystemScheduler systems = container.Resolve<SystemScheduler>();
            using AssetManager assets = container.Resolve<AssetManager>(); // indexes Resources and the project's Assets
            if (!project.Icon.IsValid)
                project.Icon = assets.Find<Texture>("Icons/Mage.svg"); // the engine's own icon unless the project names one

            using Gems gems = new(container, files);
            gems.Load(project.Gems);

            if (!TryOpenMainWindow(options, container, project, out IWindow? mainWindow))
                return 1;
            using IWindow? _ = mainWindow;

            Engine engine = new(container, files, project, systems, assets, gems, mainWindow);
            MainLoop(options, engine, shutdown.Token);
            return ExitCode(options);
        }
        catch (Exception e)
        {
            Fail($"Exception occurred, crashing...\nException:\n{e}");
            return 1;
        }
    }

    /// <summary>Ctrl+C ends the loop cleanly instead of killing the process mid-frame (gems get their Dispose, logs flush).</summary>
    private static CancellationTokenSource ListenForCtrlC()
    {
        CancellationTokenSource shutdown = new();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            shutdown.Cancel();
        };
        return shutdown;
    }

    /// <summary>
    /// The core services. The container stays open: gems register their exports into it as they load.
    /// Reuse is explicit on purpose: the default (transient) is the safe one for anything a gem registers.
    /// </summary>
    private static Container CreateContainer()
    {
        Container container = new();
        container.Register<IFileSystem, FileSystem>(Reuse.Singleton);
        container.Register<SystemScheduler>(Reuse.Singleton);
        container.Register<EventBus>(Reuse.Singleton);
        container.Register<AssetManager>(Reuse.Singleton);
        return container;
    }

    /// <summary>
    /// The engine's own root, where Resources live: the repo in a Debug or Optimize build (stamped into the
    /// assembly as MagicRoot by Magic.csproj), the exe folder in Release, where the build copied them.
    /// </summary>
    private static string FindEngineRoot(IFileSystem files)
    {
        return Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "MagicRoot" && !string.IsNullOrEmpty(a.Value) && files.DirectoryExists(a.Value))?.Value
            ?? AppContext.BaseDirectory;
    }

    /// <summary>
    /// A project is a folder with a .magic file; that folder is the root unless --root says otherwise.
    /// Without --project the engine is its own project, so a plain "dotnet run" keeps working.
    /// Gems always sit next to the exe unless --gems says otherwise.
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
            try
            {
                // From a stream so a UTF-8 BOM is skipped; from a span it would not be.
                project = JsonSerializer.Deserialize<Project>(new MemoryStream(read.Payload), AssetJson.Options) ?? new Project();
            }
            catch (JsonException ex)
            {
                return Result<Project>.Failure($"Project {path} is not valid JSON: {ex.Message}");
            }
            root = files.Parent(path)!;
            Debugging.LogInfo($"Project file: {path}");
        }
        if (options.Root is not null)
            root = files.FullPath(options.Root);

        project.Root = root;
        project.Gems = files.FullPath(options.Gems ?? files.Combine(AppContext.BaseDirectory, "Gems"));
        project.Resources = files.Combine(engineRoot, "Resources");

        return Result<Project>.Success(project);
    }

    /// <summary>
    /// Opens the main window, unless headless: then gems and systems run with no window at all.
    /// False when a window was wanted and no loaded gem can provide one.
    /// </summary>
    private static bool TryOpenMainWindow(Options options, IContainer container, Project project, out IWindow? window)
    {
        window = null;
        if (options.Headless)
            return true;

        IWindowFactory? factory = container.Resolve<IWindowFactory>(IfUnresolved.ReturnDefault);
        if (factory is null)
        {
            Debugging.LogError($"No loaded gem provides {nameof(IWindowFactory)}, so the main window cannot be opened. Is the SDL gem in {project.Gems}? Pass --headless to run without one.");
            return false;
        }

        window = factory.Create(project.Name, options.Width, options.Height, options.WindowMode);
        window.Show();

        return true;
    }

    /// <summary>
    /// Fixed timestep with an accumulator: Update and LateUpdate get the real frame time, FixedUpdate runs
    /// zero or more times per frame at a constant step. Runs until the window closes, Ctrl+C or --lifetime.
    /// </summary>
    private static void MainLoop(Options options, Engine engine, CancellationToken shutdown)
    {
        if (options.Lifetime > 0 && options.LastScreenshotFrame > options.Lifetime)
            Debugging.LogWarning($"The last screenshot is due at frame {options.LastScreenshotFrame}, after the lifetime of {options.Lifetime} frames; it will not be taken.");

        Debugging.LogInfo($"Entering the main loop on thread {Environment.CurrentManagedThreadId}{(engine.MainWindow is null ? " (headless)" : "")}{(options.Lifetime > 0 ? $" for {options.Lifetime} frames" : "")}.");

        Stopwatch clock = Stopwatch.StartNew();
        double lastMs = 0;
        double accumulatorMs = 0;
        double worstFrameMs = 0;
        long frame = 0;
        int screenshotsLeft = options.Screenshots;
        long nextScreenshot = options.ScreenshotDelay;

        while (!shutdown.IsCancellationRequested && (engine.MainWindow?.IsOpen ?? true))
        {
            frame++;
            double nowMs = clock.Elapsed.TotalMilliseconds;
            double frameMs = Math.Min(nowMs - lastMs, MaxFrameMs);
            lastMs = nowMs;
            accumulatorMs += frameMs;
            worstFrameMs = Math.Max(worstFrameMs, frameMs);

            Step(engine, frameMs, ref accumulatorMs);

            // End of frame: whatever the renderer presented this frame is what a screenshot shows.
            if (screenshotsLeft > 0 && frame >= nextScreenshot && engine.MainWindow is not null)
            {
                Screenshot(engine.Container, engine.Files, engine.Project, engine.MainWindow.Handle, frame);
                screenshotsLeft--;
                nextScreenshot += options.ScreenshotInterval;
            }

            if (options.Lifetime > 0 && frame >= options.Lifetime)
            {
                Debugging.LogInfo($"Lifetime of {options.Lifetime} frames reached.");
                break;
            }
        }

        LogSummary(frame, clock.Elapsed.TotalMilliseconds, worstFrameMs, shutdown.IsCancellationRequested);
    }

    /// <summary>One frame: pending gem and asset changes first, then Update, as many FixedUpdates as the accumulator holds, LateUpdate.</summary>
    private static void Step(Engine engine, double frameMs, ref double accumulatorMs)
    {
        // hot reloads and asset changes land here, before any system runs this frame
        engine.Gems.ProcessChanges();
        engine.Assets.ProcessChanges();

        engine.Systems.Update(frameMs);
        while (accumulatorMs >= FixedDeltaMs)
        {
            engine.Systems.FixedUpdate(FixedDeltaMs);
            accumulatorMs -= FixedDeltaMs;
        }
        engine.Systems.LateUpdate(frameMs);
    }

    private static void LogSummary(long frames, double totalMs, double worstFrameMs, bool stoppedByCtrlC)
    {
        Debugging.LogInfo($"Ran {frames} frames in {totalMs:F0} ms: {(frames == 0 ? 0 : totalMs / frames):F2} ms average, {worstFrameMs:F2} ms worst.");
        if (stoppedByCtrlC)
            Debugging.LogInfo("Stopped by Ctrl+C.");
        if (Debugging.Errors > 0)
            Debugging.LogWarning($"{Debugging.Errors} message(s) were logged at Error or above this run.");
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

    /// <summary>Captures the window's last frame and writes it to root/screenshots. Every failure is logged as an error: a screenshot that was asked for and not taken is a failed run.</summary>
    private static void Screenshot(IContainer container, IFileSystem files, Project project, uint window, long frame)
    {
        // Resolved each time: the renderer gem may have reloaded since the last one.
        IFrameCapture? capture = container.Resolve<IFrameCapture>(IfUnresolved.ReturnDefault);
        if (capture is null)
        {
            Debugging.LogError($"Screenshot at frame {frame} skipped: no loaded gem provides {nameof(IFrameCapture)}.");
            return;
        }

        Result<Frame> captured = capture.Capture(window);
        if (captured.Failed)
        {
            Debugging.LogError($"Screenshot at frame {frame} failed: {captured.Message}");
            return;
        }

        string path = files.Combine(project.Root, "Screenshots", $"{project.Name}_{frame:D6}.png");
        Result written = files.WriteBinary(path, Png.Encode(captured.Payload.Width, captured.Payload.Height, captured.Payload.Pixels));
        if (written.IsSuccess)
            Debugging.LogInfo($"Screenshot at frame {frame}: {path} ({captured.Payload.Width}x{captured.Payload.Height}).");
        else
            Debugging.LogError($"Screenshot at frame {frame} could not be written to {path}: {written.Message}");
    }
}
