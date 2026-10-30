using System.Runtime;
using Magic.Assets.Pipeline;
using Magic.CommandLine;
using Magic.Debugging;
using Magic.GemAPI;
using Magic.Projects;
using Magic.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Magic.Launcher;

internal static class Launcher
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

        var services = new ServiceCollection();
        var coreServices = services.RegisterMagicServices();
        var app = coreServices.App;
        var fileSystem = coreServices.FileSystem;

        // Run initial cmds given on startup
        Result cmdLineRunResults = Cli.Run(app, commandLine);
        if (!cmdLineRunResults.Ok)
        {
            Log.Critical($"Couldn't run the command line: {cmdLineRunResults.Error}");
            return 1;
        }

        // A job, like new or --version, is done once it ran, and failed if it logged an error.
        if (app.IsExiting)
            return Log.ErrorCount == 0 ? 0 : 1;

        // One pass per run: a command asking for another project or mode ends the run, and the next pass runs that.
        while (!app.IsExiting)
        {
            Result<Project> project = Project.Load(app.ProjectPath, fileSystem);
            if (!project.Ok)
            {
                Log.Critical(project.Error);
                return 1;
            }

            // A run's gems and assets are its own, so a restart makes them again; the host's services carry over.
            using AssetManager assets = new(
                fileSystem,
                fileSystem.Combine(fileSystem.GetParent(app.ProjectPath), "Assets")
            );
            ServiceCollection runServices = [];
            foreach (ServiceDescriptor service in services)
            {
                runServices.Add(service);
            }

            runServices.AddSingleton(assets);
            IReadOnlyList<LoadedGem> gems = GemLoader.Load(runServices, fileSystem, project.Value.Gems, assets);

            // Loads the constructors asked for start now, rather than with the first frame.
            assets.EnableAssetLoading();
            assets.Pump();
            Result ended = project.Value.Run(app, gems, assets);
            GemLoader.Unload(gems);

            if (!ended.Ok)
            {
                Log.Critical(ended.Error);
                return 2;
            }
        }

        return 0;
    }
}
