using System.Runtime;
using Magic.CommandLine;
using Magic.Debugging;
using Magic.Files;
using Magic.Host;
using Magic.Projects;
using Magic.State;

namespace Magic.Launcher;

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
    /// Runs the project the commands ask for, pumping the terminal for more, until they ask to exit. Exits 0 clean, 1
    /// for a bad command line or a project that won't open, and 2 at the first error under --fail-on-error.
    /// </summary>
    private static int Run(string[] commandLine)
    {
        Log.Info($"Command line args: {string.Join(", ", commandLine)}");
        App app = new();
        Result ran = Cli.Run(app, commandLine);
        if (!ran.Ok)
        {
            Log.Critical($"Failed to parse command-line arguments: {ran.Error}");
            return 1;
        }

        if (app.ProjectPath.Length == 0)
        {
            Log.Critical("No project to run: give one with --project <path>.");
            return 1;
        }

        IFileSystem fileSystem = new FileSystem();

        // One pass per run: a command asking for another project or mode ends the run, and the next pass runs that.
        while (!app.IsExiting)
        {
            Result<Project> project = Project.Load(app.ProjectPath, fileSystem);
            if (!project.Ok)
            {
                Log.Critical(project.Error);
                return 1;
            }

            Result ended = RunProject(app, project.Value);
            if (!ended.Ok)
            {
                Log.Critical(ended.Error);
                return 2;
            }
        }

        return 0;
    }

    /// <summary>
    /// Runs one project, pumping the terminal, until the commands ask to exit or for another project or mode. Fails at
    /// the first error under --fail-on-error.
    /// </summary>
    private static Result RunProject(App app, Project project)
    {
        Log.Info($"Running {project.Name} in {(app.IsHeadless ? "headless" : "GUI")} mode.");
        app.Start();

        // TODO(KONFUS): pump the GUI like the terminal; until there is one, a run with a window exits at once.
        if (!app.IsHeadless)
        {
            app.Exit();
            return Result.Success();
        }

        while (app.IsRunning)
        {
            Cli.Pump(app);
            if (app.FailOnError && Log.ErrorCount > 0)
                return Result.Failure($"Exiting with code 2 due to logged errors. Error count: {Log.ErrorCount}");
        }

        return Result.Success();
    }
}
