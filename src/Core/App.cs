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

namespace Magic;

/// <summary>
/// The run: which project it runs and how, and whether it goes on. Commands change it, at launch and while it runs;
/// <see cref="Run"/> acts on it.
/// </summary>
public sealed class App
{
    private readonly IFileSystem _files;
    private readonly EventCoordinator _events;
    private readonly Threads _threads;
    private readonly PipelineCoordinator _pipelines;

    /// <summary>
    /// Makes the app of one run and the commands that act on it.
    /// </summary>
    internal App(IFileSystem files, EventCoordinator events, Threads threads, PipelineCoordinator pipelines)
    {
        _files = files;
        _events = events;
        _threads = threads;
        _pipelines = pipelines;
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
            IsRunning = false;
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
    /// Whether the current run goes on: <see cref="Run"/> begins it; <see cref="Exit"/>, <see cref="Restart"/>, or a new
    /// project or mode, ends it.
    /// </summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    /// Whether the process ends once the run does.
    /// </summary>
    public bool IsExiting { get; private set; }

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
    internal void Restart() => IsRunning = false;

    /// <summary>
    /// Runs the app's project with what the host provides: its root pipelines join, then <see cref="AppStart"/> makes the
    /// gems, <see cref="Startup"/> takes it to the first frame, <see cref="AppLoop"/> runs the frames until the commands ask
    /// to exit or for another project or mode, or a gem's code changes, <see cref="Shutdown"/> follows the last frame, and
    /// <see cref="AppEnd"/> ends it. Fails when no project is open, and at the first error under --fail-on-error.
    /// </summary>
    internal Result Run(IServiceProvider provider, GemRegistry gems, AssetDatabase assets, Caches caches, World world)
    {
        if (Project is not { } project)
            return Result.Failure("No project is open.");

        _pipelines.Join(new ServicePumpPipeline(_threads, Cli));
        _pipelines.Join(new AssetPipeline(assets, _files, caches, _pipelines));
        _pipelines.Join(new GemPipeline(gems, provider, this, assets, world, _pipelines));
        _pipelines.Join(new CodePipeline(this, _files));
        _pipelines.Join(new WorldPipeline(world, assets, _threads, project, _pipelines));
        _pipelines.Join(new NextFramePipeline(this, world, _events, _pipelines));

        Log.Info($"Running {project.Name} in {(IsHeadless ? "headless" : "GUI")} mode.");
        IsRunning = true;

        // TODO(KONFUS): pump the GUI like the terminal; until there is one, a run with a window exits at once.
        if (!IsHeadless)
            Exit();

        using CancellationTokenSource stopping = new();
        _pipelines.Get<AppStart>().Run(new AppStart(project), stopping.Token);
        PumpUntil(
            _pipelines.Get<Startup>().RunOnMainAsync(new Startup(project), null, stopping.Token).AsTask(),
            stopping
        );
        _pipelines.Get<AppLoop>().Run(default, stopping.Token);
        stopping.Cancel();
        PumpUntil(
            _pipelines.Get<Shutdown>().RunOnMainInReverseAsync(new Shutdown(IsExiting), null, default).AsTask(),
            stopping
        );
        _pipelines.Get<AppEnd>().Run(default);

        if (HasFailed)
            return Result.Failure($"Exiting with code 2 due to logged errors. Error count: {Log.ErrorCount}");

        Log.Info($"Closed {project.Name} and {(IsExiting ? "exiting" : "restarting")}.");

        return Result.Success();
    }

    /// <summary>
    /// Keeps the services going until <paramref name="running"/>, an awaited pipeline outside the frames, has ended, as what
    /// it awaits needs them, cancelling <paramref name="stopping"/> once the run is to end. The one wait on the main thread:
    /// no frame is due meanwhile, and this is what keeps the rest going.
    /// </summary>
    private void PumpUntil(Task running, CancellationTokenSource stopping)
    {
        running.FireAndForget();
        PipelineRunner<ServicePump> pump = _pipelines.Get<ServicePump>();
        while (!running.IsCompleted)
        {
            pump.Run(default, stopping.Token);
            if (!IsRunning)
                stopping.Cancel();

            Thread.Sleep(1);
        }
    }
}
