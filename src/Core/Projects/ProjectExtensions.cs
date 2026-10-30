using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Magic.CommandLine;
using Magic.Debugging;
using Magic.Files;
using Magic.GemAPI;
using Magic.Host;
using Magic.Serialization;
using Magic.Utils;

namespace Magic.Projects;

public static class ProjectExtensions
{
    extension(Project project)
    {
        public static Result<Project> Load(string projectPath, IFileSystem fileSystem)
        {
            var byteReadResult = fileSystem.ReadBytes(projectPath);
            if (!byteReadResult.Ok)
                return Result.Failure(
                    $"Failed to load the project file from {projectPath}. Ex: {byteReadResult.Error}"
                );

            var jsonReadResult = Json.Read<Project>(byteReadResult.Value);
            if (!jsonReadResult.Ok)
                return Result.Failure(
                    $"Failed to load the project file from {projectPath}. Ex: {jsonReadResult.Error}"
                );

            return jsonReadResult;
        }

        /// <summary>
        /// Runs one project's frames until the commands ask to exit or for another project or mode, taking any lines typed
        /// into the terminal between frames. Fails at the first error under --fail-on-error.
        /// </summary>
        [SuppressMessage(
            "Design",
            "CA1031:Do not catch general exception types",
            Justification = "Whatever a gem throws, it is logged and the gem leaves the loop; the others go on."
        )]
        public Result Run(App app, IReadOnlyList<LoadedGem> gems)
        {
            const double FrameSec = 1.0 / 60;
            const double FixedStepSec = 1.0 / 60;
            const int MaxFixedSteps = 4;

            Log.Info($"Running {project.Name} in {(app.IsHeadless ? "headless" : "GUI")} mode.");
            app.Start();

            // TODO(KONFUS): pump the GUI like the terminal; until there is one, a run with a window exits at once.
            if (!app.IsHeadless)
            {
                app.Exit();
                return Result.Success();
            }

            // The gems still running: one that throws leaves.
            List<LoadedGem> running = [.. gems];
            Stopwatch clock = Stopwatch.StartNew();
            double lastSec = 0;
            double? pausedAtSec = null;
            double? pausedDeltaSec = null;
            double fixedSec = 0;
            double fixedOwedSec = 0;
            double nextFrameSec = 0;
            for (uint index = 0; app.IsRunning; index++)
            {
                Cli.Pump(app);

                if (app.ShouldExitOnError && Log.ErrorCount > 0)
                    return Result.Failure($"Exiting with code 2 due to logged errors. Error count: {Log.ErrorCount}");

                double nowSec = clock.Elapsed.TotalSeconds;

                Frame frame;
                if (app.IsPaused)
                {
                    pausedAtSec ??= nowSec;
                    pausedDeltaSec ??= nowSec - lastSec;
                    frame = new()
                    {
                        Index = index,
                        AppSec = nowSec,
                        AppDeltaSec = nowSec - lastSec,
                        SimSec = pausedAtSec.Value,
                        SimDeltaSec = pausedDeltaSec.Value,
                    };
                }
                else
                {
                    pausedAtSec = null;
                    pausedDeltaSec = null;
                    frame = new()
                    {
                        Index = index,
                        AppSec = nowSec,
                        AppDeltaSec = nowSec - lastSec,
                        SimSec = nowSec,
                        SimDeltaSec = nowSec - lastSec,
                    };
                }
                lastSec = nowSec;

                // Fixed steps catch the simulation up with the frame, a step at a time, at most MaxFixedSteps of them.
                fixedOwedSec += frame.AppDeltaSec;
                int fixedSteps = Math.Min((int)(fixedOwedSec / FixedStepSec), MaxFixedSteps);
                fixedOwedSec -= fixedSteps * FixedStepSec;
                if (fixedOwedSec >= FixedStepSec)
                    fixedOwedSec = 0;

                // Each gem in the order made runs its whole frame; one that throws is logged and leaves.
                for (int i = 0; i < running.Count; i++)
                {
                    LoadedGem gem = running[i];
                    try
                    {
                        gem.Gem.Tick(in frame);
                        for (int step = 1; step <= fixedSteps; step++)
                        {
                            Frame fixedFrame = frame with
                            {
                                SimSec = fixedSec + (step * FixedStepSec),
                                SimDeltaSec = FixedStepSec,
                            };
                            gem.Gem.DeterministicTick(in fixedFrame);
                        }

                        gem.Gem.Render(in frame);
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Gem {gem.Name} threw and stops running: {ex}");
                        running.RemoveAt(i);
                        i--;
                    }
                }

                fixedSec += fixedSteps * FixedStepSec;

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

            // Check if any errors occured in the last loop
            if (app.ShouldExitOnError && Log.ErrorCount > 0)
                return Result.Failure($"Exiting with code 2 due to logged errors. Error count: {Log.ErrorCount}");

            Log.Info($"Closing {project.Name} and {(app.IsExiting ? "exiting" : "restarting")}.");
            return Result.Success();
        }
    }
}
