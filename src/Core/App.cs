using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Magic.Assets;
using Magic.Assets.Pipeline;
using Magic.Caching;
using Magic.CommandLine;
using Magic.Debugging;
using Magic.Events;
using Magic.Files;
using Magic.Gems;
using Magic.Pipelines;
using Magic.Platform;
using Magic.Projects;
using Magic.Threading;
using Magic.Utils;
using Magic.Worlds;
using Microsoft.Extensions.DependencyInjection;

namespace Magic;

/// <summary>
/// The app: which project it runs and how, and whether it goes on. Commands change it, at launch and while it runs;
/// <see cref="Run"/> runs the project until the commands ask for another, for another mode, or to exit, or a gem's code
/// changes, and each run makes everything of its own anew.
/// </summary>
public sealed class App
{
    /// <summary>
    /// How long a fixed step is.
    /// </summary>
    private const double FixedStepSec = 1.0 / 60;

    private const int MaxFixedSteps = 4;

    /// <summary>
    /// How long a frame is, as nothing sets the pace yet.
    /// </summary>
    private const double FrameSec = 1.0 / 60;

    private readonly IFileSystem _files;

    /// <summary>
    /// The saved world the last run kept for the next, once written; null when it kept none.
    /// </summary>
    private Task<Handle<WorldState>>? _keptWorld;

    /// <summary>
    /// Makes the app and the commands that act on it.
    /// </summary>
    internal App(IFileSystem files)
    {
        _files = files;
        Cli = new(this, files);
    }

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
            IsRunning = value is not null;
            IsRestarting = true;
        }
    }

    /// <summary>
    /// The window the run draws into: the gem's that provides one, for as long as the run lasts; none when headless.
    /// </summary>
    public IWindow? Window { get; internal set; }

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
            IsRestarting = true;
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
    /// Whether the app runs its project: from the command that opens one until <see cref="Exit"/>, across restarts.
    /// </summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    /// Whether the process ends once the run does.
    /// </summary>
    public bool IsExiting { get; private set; }

    /// <summary>
    /// Whether the current run ends to be run again, with everything made anew: <see cref="Restart"/>, a new project
    /// or a new mode asks for it, and the next run clears it as it begins.
    /// </summary>
    internal bool IsRestarting { get; private set; }

    /// <summary>
    /// The commands that act on this app: the command line, and each line typed into the terminal.
    /// </summary>
    internal Cli Cli { get; }

    /// <summary>
    /// Whether --fail-on-error has seen its error.
    /// </summary>
    internal bool HasFailed => ShouldExitOnError && Log.ErrorCount > 0;

    public void Exit()
    {
        IsExiting = true;
        IsRunning = false;
    }

    /// <summary>
    /// Ends the run, and Magic runs the project again with everything made anew.
    /// </summary>
    internal void Restart() => IsRestarting = true;

    /// <summary>
    /// Runs the project once: makes the run's services and its root pipelines, has <see cref="AppStart"/> index the
    /// assets, make the gems and start the world filling, runs the frames until the run ends, and has
    /// <see cref="AppEnd"/> keep the world for a restart; then everything of the run's is disposed, the gems last made
    /// first. Fails when no project is open, and at the first error under --fail-on-error.
    /// </summary>
    [SuppressMessage(
        "Usage",
        "VSTHRD002:Avoid problematic synchronous waits",
        Justification = "The world the last run kept is waited for between runs, once its threads have ended; its "
            + "main-thread part ran before they did and the rest is written on a worker, so the wait needs no thread it "
            + "holds up."
    )]
    internal Result Run()
    {
        IsRestarting = false;
        if (Project is not { } project)
            return Result.Failure("No project is open.");

        // The one wait on the main thread: this run's assets must find the kept world on disk before they index.
        Handle<WorldState> kept = _keptWorld is null ? Handle<WorldState>.None : _keptWorld.GetAwaiter().GetResult();
        Threads threads = new();
        EventCoordinator events = new();
        PipelineCoordinator pipelines = new(threads);
        Caches caches = new(_files, project.Name, _files.GetParent(project.Path));
        using AssetDatabase assets = new(_files, pipelines, caches, project);
        using World world = new(assets, threads, pipelines);
        Cli.Events = events;
        Cli.Assets = assets;
        Cli.World = world;

        ServiceCollection services = [];
        services.AddSingleton(this);
        services.AddSingleton(_files);
        services.AddSingleton(events);
        services.AddSingleton(threads);
        services.AddSingleton(pipelines);
        services.AddSingleton(caches);
        services.AddSingleton(assets);
        services.AddSingleton(world);
        using GemRegistry registry = new(services, _files, assets, project.Gems);
        services.AddSingleton(
            new Assemblies([typeof(App).Assembly, .. registry.Gems.Select(gem => gem.Type.Assembly)])
        );

        // Never disposed, since its singletons are the gems, which the registry ends.
        services.MakeReadOnly();
        IServiceProvider provider = services.BuildServiceProvider();

        using Subscriptions registered =
        [
            pipelines.Register(new ServicePumpPipeline(threads, Cli)),
            pipelines.Register(new AssetPipeline(assets, pipelines)),
            pipelines.Register(new GemPipeline(registry, provider, this, world, pipelines)),
            pipelines.Register(new WorldPipeline(world, assets, threads, project, pipelines)),
        ];

        Log.Info($"Running {project.Name} in {(IsHeadless ? "headless" : "GUI")} mode.");
        using CancellationTokenSource stopping = new();
        pipelines.Run(new AppStart(project, kept), stopping.Token);
        if (!IsHeadless && Window is null)
        {
            Log.Warn(
                $"{project.Name} has no gem that provides a window, so the run is exiting. List one, or use --headless."
            );
            Exit();
        }

        RunFrames(world, events, pipelines, stopping.Token);
        stopping.Cancel();
        _keptWorld = pipelines.Run(new AppEnd(IsExiting)).KeptWorld;
        Cli.Events = null;
        Cli.Assets = null;
        Cli.World = null;

        if (HasFailed)
            return Result.Failure($"Exiting with code 2 due to logged errors. Error count: {Log.ErrorCount}");

        Log.Info($"Closed {project.Name} and {(IsExiting ? "exiting" : "restarting")}.");

        return Result.Success();
    }

    /// <summary>
    /// Sleeps until the next frame is due: when it is due, as fixed steps of <see cref="FrameSec"/>, so a sleep the OS rounds
    /// up to its timer's tick is made up by the next one; a frame late by more than a frame starts the count again rather
    /// than run frames back to back.
    /// </summary>
    private static double SleepUntilDue(Stopwatch clock, double lastDueSec)
    {
        double dueSec = lastDueSec + FrameSec;
        double leftSec = dueSec - clock.Elapsed.TotalSeconds;
        if (leftSec > 0)
            Thread.Sleep(TimeSpan.FromSeconds(leftSec));
        else if (leftSec < -FrameSec)
            dueSec = clock.Elapsed.TotalSeconds;

        return dueSec;
    }

    /// <summary>
    /// The frame loop, until the run ends: works out the frame's clocks, runs the services' pump and then the frame's
    /// pipelines in their order, with the fixed steps the clock owes, and sleeps until the next frame is due. Time
    /// begins with the first frame, not with what was loaded before it.
    /// </summary>
    private void RunFrames(
        World world,
        EventCoordinator events,
        PipelineCoordinator pipelines,
        CancellationToken cancel
    )
    {
        Stopwatch clock = Stopwatch.StartNew();
        uint index = 0;
        double lastSec = 0;
        double simSec = 0;
        double fixedSec = 0;
        double owedSec = 0;
        double dueSec = 0;
        while (IsRunning && !IsRestarting && !HasFailed)
        {
            // While the world loads, its time runs as it is, as the gems may need steps to get ready.
            double nowSec = clock.Elapsed.TotalSeconds;
            bool isPaused = IsPaused || (world.IsPaused && !world.IsLoading);
            double simDeltaSec = isPaused ? 0 : (nowSec - lastSec) * (world.IsLoading ? 1 : world.TimeScale);
            simSec += simDeltaSec;

            // Fixed steps catch the simulation up with the frame, a step at a time, at most MaxFixedSteps of them; what
            // is owed past that is dropped.
            owedSec += simDeltaSec;
            int fixedSteps = Math.Min((int)(owedSec / FixedStepSec), MaxFixedSteps);
            owedSec -= fixedSteps * FixedStepSec;
            if (owedSec >= FixedStepSec)
                owedSec = 0;

            Frame frame = new()
            {
                Index = index++,
                AppSec = nowSec,
                AppDeltaSec = nowSec - lastSec,
                SimSec = simSec,
                SimDeltaSec = simDeltaSec,
                IsPaused = isPaused,
            };
            lastSec = nowSec;

            pipelines.Run<ServicePump>(cancel);
            frame = frame with { Events = events.Take() };
            pipelines.Run(new Update(frame), cancel);
            for (int step = 1; step <= fixedSteps; step++)
            {
                Frame fixedFrame = frame with
                {
                    SimSec = fixedSec + (step * FixedStepSec),
                    SimDeltaSec = FixedStepSec,
                    Events = FrameEvents.None,
                };
                pipelines.Run(new FixedUpdate(fixedFrame), cancel);
            }

            fixedSec += fixedSteps * FixedStepSec;
            pipelines.Run(new LateUpdate(frame), cancel);
            pipelines.Run(new Render(frame), cancel);
            dueSec = SleepUntilDue(clock, dueSec);
        }
    }
}
