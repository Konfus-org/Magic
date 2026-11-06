using System.Diagnostics.Metrics;
using System.Runtime;
using Magic.Caching;
using Magic.CommandLine;
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
    /// Runs what the commands ask for, a job like new or --version, or a project, pumping the terminal for more until
    /// they ask to exit. Exits 0 clean; 1 for a bad command line, a job that failed or a project that won't open; and 2
    /// at the first error under --fail-on-error.
    /// </summary>
    private static int Run(string[] commandLine)
    {
        Log.Verbose($"Command line args: {string.Join(", ", commandLine)}");

        // What outlasts a run: the app, which the commands set, and the world the last run kept.
        using App app = new();
        byte[]? kept = null;
        bool isLaunching = true;

        // One pass per run, all new for it: a typed command that asks for another project or mode, or a change to a gem's
        // code, ends the run, and the next starts anew.
        while (true)
        {
            FileSystem files = new();
            EventCoordinator events = new();
            Threads threads = new();
            Caches caches = new(files, app);
            using Assets assets = new(files, caches, app);
            using World world = new(assets, files, threads, app);
            Cli cli = new(app, files, events, assets, world);

            if (isLaunching)
            {
                isLaunching = false;
                Result ran = cli.Execute(commandLine);
                if (!ran.Ok)
                {
                    Log.Critical($"Couldn't run the command line: {ran.Error}");
                    return 1;
                }
            }

            if (app.HasFailed)
                return ExitFailed();

            // A job, like new or --version, is done once it ran, and failed if it logged an error, as a project that
            // won't open does.
            if (app.IsExiting || app.Project is not { } project)
                return Log.ErrorCount == 0 ? 0 : 1;

            // The project's folder comes first, as the first winning a path two directories have.
            assets.Register(files.GetParent(project.Path));

            // The services, then the gems the project lists, which join them.
            ServiceCollection services = [];
            services.AddSingleton(app);
            services.AddSingleton<IFileSystem>(files);
            services.AddSingleton(events);
            services.AddSingleton(threads);
            services.AddSingleton(caches);
            services.AddSingleton(assets);
            services.AddSingleton(world);
            List<GemInfo> registered = GemRegistry.Register(files, project.Gems, services);

            // Never disposed, since its singletons are the gems, which Gems ends.
            services.MakeReadOnly();
            IServiceProvider provider = services.BuildServiceProvider();

            Log.Info($"Running {project.Name} in {(app.IsHeadless ? "headless" : "GUI")} mode.");

            app.StartRun();
            using Gems gems = new(registered, provider, app, files, assets, world, events, threads);
            world.StartAsync(kept, app.Lifetime).FireAndForget();

            // If we don't have a window and aren't headless something is wrong, early out.
            if (!app.IsHeadless && app.Window is null)
            {
                Log.Warn(
                    $"{project.Name} has no gem that provides a window, so the run is exiting. List one, or use --headless."
                );
                app.Exit();
            }

            while (!app.Lifetime.IsCancellationRequested)
            {
                // Under --fail-on-error, the first error logged ends the run; the exit code says so below.
                if (app.HasFailed)
                {
                    app.Exit();
                    continue;
                }

                // Core's own steps are timed for whatever listens; the gems' pump times its hooks itself.
                long start = _stepDuration.Start();
                threads.Pump(EngineThread.Main);
                _stepDuration.RecordSince(start, new KeyValuePair<string, object?>(Metrics.StepTag, "Threads"));
                start = _stepDuration.Start();
                assets.ProcessChanges();
                _stepDuration.RecordSince(start, new KeyValuePair<string, object?>(Metrics.StepTag, "Assets"));
                start = _stepDuration.Start();
                world.Pump();
                _stepDuration.RecordSince(start, new KeyValuePair<string, object?>(Metrics.StepTag, "World"));
                gems.Pump();
                start = _stepDuration.Start();
                cli.Pump();
                _stepDuration.RecordSince(start, new KeyValuePair<string, object?>(Metrics.StepTag, "Commands"));
            }

            if (app.HasFailed)
                return ExitFailed();

            // The world is kept while the gems are still whole, for a restart of this same project; they end as this pass
            // does, last made first.
            kept = app.IsExiting || app.Project != project ? null : world.Keep();

            Log.Info($"Closing {project.Name} and {(app.IsExiting ? "exiting" : "restarting")}.");
            if (app.IsExiting)
                return 0;
        }

        static int ExitFailed()
        {
            Log.Critical($"Exiting with code 2 due to logged errors. Error count: {Log.ErrorCount}");

            return 2;
        }
    }
}
