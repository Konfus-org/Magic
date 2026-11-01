using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Magic.Assets.Pipeline;
using Magic.CommandLine;
using Magic.Debugging;
using Magic.Files;
using Magic.GemAPI;
using Magic.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Magic.Runtime;

/// <summary>
/// The run: which project it runs and how, and whether it goes on. Commands change it, at launch and while it runs;
/// <see cref="Run"/> acts on it.
/// </summary>
public sealed class App
{
    /// <summary>
    /// The project to run, as read from its <c>.magic</c> file; none until one is opened. Opening another ends the run,
    /// and Magic runs the new project next; opening the same file again changes nothing.
    /// </summary>
    public Project? Project
    {
        get;
        set
        {
            if (value?.Path == field?.Path)
                return;

            field = value;
            IsRunning = false;
        }
    }

    /// <summary>
    /// The window the run draws into: the gem's that provides one, for as long as the run lasts; none when headless.
    /// </summary>
    public IWindow? Window { get; private set; }

    /// <summary>
    /// Whether the run has no window. Changing it ends the run, and Magic runs it again in the new mode.
    /// </summary>
    public bool IsHeadless
    {
        get;
        set
        {
            if (value == field)
                return;

            field = value;
            IsRunning = false;
        }
    }

    /// <summary>
    /// Whether the process exits with 2 as soon as an error is logged.
    /// </summary>
    public bool ShouldExitOnError { get; set; }

    /// <summary>
    /// Pauses the application, simulation systems will not be run until this is set back to true.
    /// Should not be used for a gameplay pause.
    /// </summary>
    public bool IsPaused { get; set; }

    /// <summary>
    /// Whether the current run goes on: <see cref="Run"/> begins it; <see cref="Exit"/>, or a new project or mode, ends
    /// it.
    /// </summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    /// Whether the process ends once the run does.
    /// </summary>
    public bool IsExiting { get; private set; }

    public void Exit()
    {
        IsExiting = true;
        IsRunning = false;
    }

    /// <summary>
    /// Runs the app's project: adds its folder to <paramref name="assets"/>, registers its gems with
    /// <paramref name="services"/>, which holds what the host provides, indexes the assets, makes and sets up the
    /// gems, and runs their frames until the commands ask to exit or for another project or mode, then unloads them.
    /// Runs a line typed into the terminal, pumps <paramref name="assets"/> and takes what was published on
    /// <paramref name="events"/> between frames. The gem that provides
    /// <see cref="IWindow"/> is the app's <see cref="Window"/> for the run. Fails when no project is open, and at
    /// the first error under --fail-on-error.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever a gem throws, it is logged and the gem leaves the loop; the others go on."
    )]
    internal Result Run(ServiceCollection services, IFileSystem files, AssetManager assets, Events events)
    {
        const double FrameSec = 1.0 / 60;
        const double FixedStepSec = 1.0 / 60;
        const int MaxFixedSteps = 4;

        if (Project is not { } project)
            return Result.Failure("No project is open.");

        assets.AddAssetDirectory(files.GetParent(project.Path));
        GemRegistry registry = new(services, files, project.Gems, assets);

        // Every asset directory is indexed before any gem is made, so a constructor can find assets.
        assets.Index();

        // Never disposed, since its singletons are the gems, which Unload ends.
        services.MakeReadOnly();
        IServiceProvider provider = services.BuildServiceProvider();
        IReadOnlyList<LoadedGem> gems = GemLoader.Load(provider, registry, assets);
        Window = GetWindow(provider);

        Log.Info($"Running {project.Name} in {(IsHeadless ? "headless" : "GUI")} mode.");
        IsRunning = true;

        // TODO(KONFUS): pump the GUI like the terminal; until there is one, a run with a window exits at once.
        if (!IsHeadless)
            Exit();

        // The gems still running: one that throws leaves.
        List<LoadedGem> running = [.. gems];
        Stopwatch clock = Stopwatch.StartNew();
        double lastSec = 0;
        double? pausedAtSec = null;
        double? pausedDeltaSec = null;
        double fixedSec = 0;
        double fixedOwedSec = 0;
        double nextFrameSec = 0;
        for (uint index = 0; IsRunning; index++)
        {
            Cli.Pump(this, files);
            assets.Pump();
            FrameEvents published = events.Take();

            if (ShouldExitOnError && Log.ErrorCount > 0)
                break;

            double nowSec = clock.Elapsed.TotalSeconds;

            Frame frame;
            if (IsPaused)
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
                    Events = published,
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
                    Events = published,
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
                            Events = FrameEvents.None,
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

        bool isFailed = ShouldExitOnError && Log.ErrorCount > 0;
        if (!isFailed)
            Log.Info($"Closing {project.Name} and {(IsExiting ? "exiting" : "restarting")}.");

        // The window is one of the gems, which can't unload while the app holds it.
        Window = null;
        GemLoader.Unload(gems);

        return isFailed
            ? Result.Failure($"Exiting with code 2 due to logged errors. Error count: {Log.ErrorCount}")
            : Result.Success();
    }

    /// <summary>
    /// The window the gems provide; none when no gem provides one, or the one that does wasn't made.
    /// </summary>
    private static IWindow? GetWindow(IServiceProvider provider)
    {
        try
        {
            return provider.GetService<IWindow>();
        }
        catch (InvalidOperationException)
        {
            // The gem loader has logged why it wasn't made.
            return null;
        }
    }
}
