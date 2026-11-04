using System.Runtime;
using Magic.Debugging;
using Magic.Files;
using Magic.Utils;

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
    /// Runs what the commands ask for, a job like new or --version, or a project, run again after each restart, until
    /// they ask to exit. Exits 0 clean; 1 for a bad command line, a job that failed or a project that won't open; and 2
    /// at the first error under --fail-on-error.
    /// </summary>
    private static int Run(string[] commandLine)
    {
        Log.Verbose($"Command line args: {string.Join(", ", commandLine)}");
        App app = new(new FileSystem());
        Result ran = app.Cli.Execute(commandLine);
        if (!ran.Ok)
        {
            Log.Critical($"Couldn't run the command line: {ran.Error}");
            return 1;
        }

        while (app.IsRunning)
        {
            Result ended = app.Run();
            if (!ended.Ok)
            {
                Log.Critical(ended.Error);
                return 2;
            }
        }

        // A job, like new or --version, is done once it ran, and failed if it logged an error, as a project that won't
        // open does.
        if (app.Project is null)
            return Log.ErrorCount == 0 ? 0 : 1;

        // A gem may log an error as it ends, after its run has passed.
        if (app.HasFailed)
        {
            Log.Critical($"Exiting with code 2 due to logged errors. Error count: {Log.ErrorCount}");
            return 2;
        }

        return 0;
    }
}
