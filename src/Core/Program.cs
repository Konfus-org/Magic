using System.Diagnostics.CodeAnalysis;
using System.Runtime;
using Magic.Assets.Pipeline;
using Magic.Caching;
using Magic.CommandLine;
using Magic.Debugging;
using Magic.Files;
using Magic.GemAPI;
using Magic.Runtime;
using Magic.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Magic;

internal static class Program
{
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

        // One pass per run, all new for it: a typed command that asks for another project or mode ends the run, and the
        // next starts anew from the command line and the commands like it.
        while (true)
        {
            ServiceCollection services = [];
            (App app, IFileSystem files, Events events) = RegisterRequiredServices(services);
            Result ran = Cli.Run(app, files, commandLine);
            if (!ran.Ok)
            {
                Log.Critical($"Couldn't run the command line: {ran.Error}");
                return 1;
            }

            // A job, like new or --version, is done once it ran, and failed if it logged an error, as a project that
            // won't open does.
            if (app.IsExiting || app.Project is not { } project)
                return Log.ErrorCount == 0 ? 0 : 1;

            using AssetManager assets = RegisterProjectServices(services, files, project);
            Result ended = app.Run(services, files, assets, events);
            if (!ended.Ok)
            {
                Log.Critical(ended.Error);
                return 2;
            }

            if (app.IsExiting)
                return 0;
        }
    }

    /// <summary>
    /// Makes and registers with <paramref name="services"/> what Magic provides a run before it knows the project, all
    /// new for it: the app, the file system and the gems' messages.
    /// </summary>
    private static (App App, IFileSystem Files, Events Events) RegisterRequiredServices(ServiceCollection services)
    {
        App app = new();
        FileSystem files = new();
        Events events = new();
        services.AddSingleton(app);
        services.AddSingleton<IFileSystem>(files);
        services.AddSingleton(events);

        return (app, files, events);
    }

    /// <summary>
    /// Makes and registers with <paramref name="services"/> what Magic provides a run of <paramref name="project"/>: its
    /// caches and the assets, which the caller ends with the run.
    /// </summary>
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The assets go to the caller, which ends them with the run."
    )]
    private static AssetManager RegisterProjectServices(ServiceCollection services, IFileSystem files, Project project)
    {
        Caches caches = new(files, project.Name, files.GetParent(project.Path));
        AssetManager assets = new(files, caches);
        services.AddSingleton(caches);
        services.AddSingleton(assets);

        return assets;
    }
}
