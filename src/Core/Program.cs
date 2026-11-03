using System.Linq;
using System.Runtime;
using Magic.Assets.Pipeline;
using Magic.Caching;
using Magic.Debugging;
using Magic.Events;
using Magic.Files;
using Magic.Gems;
using Magic.Pipelines;
using Magic.Threading;
using Magic.Utils;
using Magic.Worlds;
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

        // One pass per run, all new for it: a typed command that asks for another project or mode, or a change to a gem's
        // code, ends the run, and the next starts anew from the command line and the commands like it.
        while (true)
        {
            FileSystem files = new();
            EventCoordinator events = new();
            Threads threads = new();
            PipelineCoordinator pipelines = new(threads);
            App app = new(files, events, threads, pipelines);

            // Setup required core services
            ServiceCollection services = [];
            services.AddSingleton(app);
            services.AddSingleton<IFileSystem>(files);
            services.AddSingleton(events);
            services.AddSingleton(threads);

            // Runs initial arguments
            Result ran = app.Cli.Execute(commandLine);
            if (!ran.Ok)
            {
                Log.Critical($"Couldn't run the command line: {ran.Error}");
                return 1;
            }

            // A job, like new or --version, is done once it ran, and failed if it logged an error, as a project that
            // won't open does.
            if (app.IsExiting || app.Project is not { } project)
                return Log.ErrorCount == 0 ? 0 : 1;

            // Setup services that require a project
            Caches caches = new(files, project.Name, files.GetParent(project.Path));
            using AssetDatabase assets = new(files, pipelines);
            using World world = new(assets, threads, pipelines);
            services.AddSingleton(caches);
            services.AddSingleton(pipelines);
            services.AddSingleton(assets);
            services.AddSingleton(world);

            // Find the gems the project lists, which join the services
            GemRegistry gems = new(services, files, project.Gems);
            services.AddSingleton(
                new Assemblies([typeof(App).Assembly, .. gems.Gems.Select(gem => gem.Type.Assembly)])
            );

            // Never disposed, since its singletons are the gems, which AppEnd unloads.
            services.MakeReadOnly();
            IServiceProvider provider = services.BuildServiceProvider();

            // Run the application
            Result ended = app.Run(provider, gems, assets, caches, world);
            if (!ended.Ok)
            {
                Log.Critical(ended.Error);
                return 2;
            }

            if (app.IsExiting)
                return 0;
        }
    }
}
