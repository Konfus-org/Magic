using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime;
using Magic.CommandLine;
using Magic.Debugging;
using Magic.Files;
using Magic.GemAPI;
using Magic.Host;
using Magic.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Magic.Launcher;

internal static class Launcher
{
    /// <summary>
    /// How far apart frames are due while nothing else sets the pace: 60 a second, on average.
    /// </summary>
    private const double FrameSec = 1.0 / 60;

    private const double FixedStepSec = 1.0 / 60;

    /// <summary>
    /// The most fixed steps a frame runs; time owed past them is dropped, so a slow frame doesn't make the next slower.
    /// </summary>
    private const int MaxFixedSteps = 4;

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
        App app = new();
        Result cmdLineRunResults = Cli.Run(app, commandLine);
        Log.Verbose($"Command line args: {string.Join(", ", commandLine)}");
        if (!cmdLineRunResults.Ok)
        {
            Log.Critical($"Couldn't run the command line: {cmdLineRunResults.Error}");
            return 1;
        }

        // A job, like new or --version, is done once it ran, and failed if it logged an error.
        if (app.IsExiting)
            return Log.ErrorCount == 0 ? 0 : 1;

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

            if (project.Value.Gems.Length > 0)
                Log.Info($"Loading {string.Join(", ", project.Value.Gems)}.");

            IReadOnlyList<LoadedGem> gems = GemLoader.Load(new ServiceCollection(), fileSystem, project.Value);
            Result ended = RunProject(app, project.Value, gems);
            Log.Info($"Closing {project.Value.Name} and {(app.IsExiting ? "exiting" : "restarting")}.");

            // Last made, first ended, so no gem outlives one it was made after.
            for (int i = gems.Count - 1; i >= 0; i--)
            {
                Log.Info($"Unloading {gems[i].Name}.");
                gems[i].Dispose();
            }

            if (!ended.Ok)
            {
                Log.Critical(ended.Error);
                return 2;
            }
        }

        return 0;
    }

    /// <summary>
    /// Runs one project's frames until the commands ask to exit or for another project or mode, taking any lines typed
    /// into the terminal between frames. Fails at the first error under --fail-on-error.
    /// </summary>
    private static Result RunProject(App app, Project project, IReadOnlyList<LoadedGem> gems)
    {
        Log.Info($"Running {project.Name} in {(app.IsHeadless ? "headless" : "GUI")} mode.");
        app.Start();

        // TODO(KONFUS): pump the GUI like the terminal; until there is one, a run with a window exits at once.
        if (!app.IsHeadless)
        {
            app.Exit();
            return Result.Success();
        }

        // The gems still running: one that throws in a hook leaves.
        List<LoadedGem> running = [.. gems];
        Stopwatch clock = Stopwatch.StartNew();
        double lastSec = 0;
        double fixedSec = 0;
        double fixedOwedSec = 0;
        double nextFrameSec = 0;
        for (uint index = 0; ; index++)
        {
            Cli.Pump(app);
            if (app.ShouldExitOnError && Log.ErrorCount > 0)
                return Result.Failure($"Exiting with code 2 due to logged errors. Error count: {Log.ErrorCount}");

            if (!app.IsRunning)
                return Result.Success();

            double nowSec = clock.Elapsed.TotalSeconds;
            Frame frame = new()
            {
                Index = index,
                AppSec = nowSec,
                AppDeltaSec = nowSec - lastSec,
                SimSec = nowSec,
                SimDeltaSec = nowSec - lastSec,
            };
            lastSec = nowSec;
            RunHook(running, frame, static (gem, frame) => gem.Step(in frame), isReversed: false);

            // Fixed steps catch the simulation up with the frame, a step at a time, at most MaxFixedSteps of them.
            fixedOwedSec += frame.AppDeltaSec;
            for (int steps = 0; steps < MaxFixedSteps && fixedOwedSec >= FixedStepSec; steps++)
            {
                fixedSec += FixedStepSec;
                fixedOwedSec -= FixedStepSec;
                RunHook(
                    running,
                    frame with
                    {
                        SimSec = fixedSec,
                        SimDeltaSec = FixedStepSec,
                    },
                    static (gem, frame) => gem.FixedStep(in frame),
                    isReversed: false
                );
            }

            if (fixedOwedSec >= FixedStepSec)
                fixedOwedSec = 0;

            RunHook(running, frame, static (gem, frame) => gem.Render(in frame), isReversed: true);

            // Nothing sets the pace yet, so the loop sleeps until the next frame is due rather than spin. Due times
            // are fixed steps of FrameSec, so a sleep the OS rounds up to its timer's tick is made up by the next one;
            // a frame late by more than a frame starts the count again rather than run frames back to back.
            nextFrameSec += FrameSec;
            double leftSec = nextFrameSec - clock.Elapsed.TotalSeconds;
            if (leftSec > 0)
                Thread.Sleep(TimeSpan.FromSeconds(leftSec));
            else if (leftSec < -FrameSec)
                nextFrameSec = clock.Elapsed.TotalSeconds;
        }
    }

    /// <summary>
    /// Runs <paramref name="hook"/> on each of <paramref name="running"/>, last made first when
    /// <paramref name="isReversed"/>. A gem that throws is logged and taken out, and the others go on.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever a gem's hook throws, it is logged and the gem leaves the loop; the others go on."
    )]
    private static void RunHook(List<LoadedGem> running, Frame frame, Action<IGem, Frame> hook, bool isReversed)
    {
        // n counts the gems left to run, so taking one out never skips the next, in either order.
        for (int n = running.Count - 1; n >= 0; n--)
        {
            int i = isReversed ? n : running.Count - 1 - n;
            try
            {
                hook(running[i].Gem, frame);
            }
            catch (Exception ex)
            {
                // The exception's stack names the hook it was thrown in.
                Log.Error($"Gem {running[i].Name} threw and stops running: {ex}");
                running.RemoveAt(i);
            }
        }
    }
}
