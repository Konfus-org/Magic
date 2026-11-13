using System.Diagnostics.Metrics;
using System.Runtime;
using Magic.Assets;
using Magic.CommandLine;
using Magic.Configuration;
using Magic.Debugging;
using Magic.Events;
using Magic.Files;
using Magic.Projects;
using Magic.Threading;
using Magic.Utils;
using Magic.Worlds;
using Microsoft.Extensions.DependencyInjection;

namespace Magic;

internal static class Program
{
    /// <summary>
    /// How long the platform thread waits between checks for posted work, when no platform gem waits on the OS.
    /// </summary>
    private static readonly TimeSpan _platformWait = TimeSpan.FromMilliseconds(10);

    private static readonly Histogram<double> _stepDuration = Metrics.Meter.CreateHistogram<double>(
        Metrics.StepDuration,
        "s",
        "One step of Core's own in the run's loop."
    );

    private static int Main(string[] commandLine)
    {
        GCSettings.LatencyMode = GCLatencyMode.LowLatency;

        // An exception nothing caught, on any thread, crashes, leaving a crash file.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Assert.Now($"Unhandled exception: {e.ExceptionObject}");

        int exitCode = Run(commandLine);
        Log.Flush();

        return exitCode;
    }

    /// <summary>
    /// Runs what the commands ask for, a job like new or --version, or a project, and pumps the terminal until they ask
    /// to exit.
    /// </summary>
    /// <remarks>
    /// It returns 0 for a clean exit; 1 for a bad command line, a job that failed or a project that does not open; and
    /// 2 at the first error under --fail-on-error.
    /// </remarks>
    private static int Run(string[] commandLine)
    {
        Log.Verbose($"Command line args: {string.Join(", ", commandLine)}");

        // What outlasts a run: the app, which the commands set, the gems' settings, and the world as the last run saved
        // it.
        using App app = new();
        Settings settings = new(app, new FileSystem());
        Task<Result<byte[]>>? worldRestorePoint = null;
        string[]? launchCommands = commandLine;

        // One pass per run, all new for it: a typed command that asks for another project or mode, or a change to the
        // code of a gem, ends the run, and the next run starts new.
        while (true)
        {
            (int? exitCode, worldRestorePoint) = RunPass(app, settings, launchCommands, worldRestorePoint);
            if (exitCode is { } code)
                return code;

            launchCommands = null;
        }
    }

    /// <summary>
    /// Runs one pass, all new for it: the command line first when it is given, then the project until its run ends.
    /// </summary>
    /// <remarks>
    /// It returns the exit code of Run, or null and the world as this run saved it when the next run must start.
    /// It is a method of its own because, in Run's loop, CA2000 falsely reports the world as not disposed
    /// (dotnet/roslyn-analyzers#7047).
    /// </remarks>
    private static (int? ExitCode, Task<Result<byte[]>>? WorldRestorePoint) RunPass(
        App app,
        Settings settings,
        string[]? launchCommands,
        Task<Result<byte[]>>? worldRestorePoint
    )
    {
        FileSystem files = new();
        EventCoordinator events = new();
        Threads threads = new();
        Caches caches = new(files, app);
        using AssetManager assets = new(files, caches, events, app);
        using World world = new(assets, files, events, threads, app);
        Cli cli = new(app, settings, files, events, assets, world);

        if (launchCommands is not null)
        {
            Result ran = cli.Execute(launchCommands);
            if (!ran.Ok)
            {
                Log.Critical($"Couldn't run the command line: {ran.Error}");
                return (1, null);
            }

            // Only the command line gives the owner, so Magic watches it from the first pass to the end.
            if (app.Owner is { } owner)
                AppOwnerLink.WatchAsync(app, owner).FireAndForget();
        }

        if (app.HasFailed)
            return (ExitFailed(), null);

        // A job, like new or --version, is done once it ran, and failed if it logged an error, as a project that
        // won't open does.
        if (app.IsExiting || app.Project is not { } project)
            return (Log.ErrorCount == 0 ? 0 : 1, null);

        // The project's folder comes first, as the first winning a path two directories have.
        assets.Register(files.GetParent(project.Path));

        // The services, which the gems the project lists join as they are made.
        ServiceCollection services = [];
        services.AddSingleton(app);
        services.AddSingleton(settings);
        services.AddSingleton<IFileSystem>(files);
        services.AddSingleton(events);
        services.AddSingleton(threads);
        services.AddSingleton(caches);
        services.AddSingleton(assets);
        services.AddSingleton(world);

        Log.Info($"Running {project.Name} in {(app.IsHeadless ? "headless" : "GUI")} mode.");

        app.StartRun();
        using Gems gems = new(project.Gems, services, app, settings, files, assets, world, events, threads);
        settings.ReportUnknownKeys();
        world.StartAsync(worldRestorePoint?.WaitAsync(app.Lifetime), Terminal.Progress, app.Lifetime).FireAndForget();

        // A run that is not headless cannot continue without a window.
        if (!app.IsHeadless && app.Window is null)
        {
            Log.Warn(
                $"{project.Name} has no gem that provides a window, so the run is exiting. List one, or use --headless."
            );
            app.Exit();
        }

        // The frames run on the game thread, while this one, the platform thread, pumps the OS's events until the
        // run ends; then the game thread's last frame finishes, and this thread is the game thread for what ends.
        threads.Start(EngineThread.Game, () => RunFrame(app, world, gems, cli));
        while (!app.Lifetime.IsCancellationRequested)
        {
            threads.Pump(EngineThread.Platform);
            if (app.Platform is { } platform)
                platform.Pump();
            else
                app.Lifetime.WaitHandle.WaitOne(_platformWait);
        }

        threads.Stop(EngineThread.Game);
        if (app.HasFailed)
            return (ExitFailed(), null);

        // The world is saved while the gems are still whole, for a restart of this same project; they end as this pass
        // does, last made first.
        Task<Result<byte[]>>? saved = app.IsExiting || app.Project != project ? null : world.SaveAsync();
        Log.Info($"Closing {project.Name} and {(app.IsExiting ? "exiting" : "restarting")}.");

        return (app.IsExiting ? 0 : null, saved);

        // One frame on the game thread, after what was posted to it: Core's own steps are timed for whatever listens,
        // and the pump of the gems times their hooks itself. Under --fail-on-error, the first error logged ends the
        // run, and the exit code says so.
        static void RunFrame(App app, World world, Gems gems, Cli cli)
        {
            if (app.HasFailed)
                app.Exit();

            // The platform thread ends the game thread once it next wakes; until then no frame runs for an ended run.
            if (app.Lifetime.IsCancellationRequested)
                return;

            long start = _stepDuration.Start();
            world.Pump();
            _stepDuration.RecordSince(start, new KeyValuePair<string, object?>(Metrics.StepTag, "World"));
            gems.Pump();
            start = _stepDuration.Start();
            cli.Pump();
            _stepDuration.RecordSince(start, new KeyValuePair<string, object?>(Metrics.StepTag, "Commands"));
        }

        static int ExitFailed()
        {
            Log.Critical($"Exiting with code 2 due to logged errors. Error count: {Log.ErrorCount}");

            return 2;
        }
    }
}
