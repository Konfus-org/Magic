using System.Diagnostics;
using Magic.Assets.Pipeline;
using Magic.Debugging;
using Magic.Files;
using Magic.GemAPI;
using Magic.Pipelines;
using Magic.Threading;
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

    /// <summary>
    /// Whether --fail-on-error has seen its error.
    /// </summary>
    private bool HasFailed => ShouldExitOnError && Log.ErrorCount > 0;

    public void Exit()
    {
        IsExiting = true;
        IsRunning = false;
    }

    /// <summary>
    /// Runs the app's project: adds its folder to <paramref name="assets"/>, registers its gems with
    /// <paramref name="services"/>, which holds what the host provides, indexes the assets, makes and sets up the
    /// gems, then runs its pipelines: <see cref="Startup"/> once, the core pipeline until the commands ask to exit or for
    /// another project or mode, and <see cref="Shutdown"/> once, then unloads the gems. The gem that provides
    /// <see cref="IWindow"/> is the app's <see cref="Window"/> for the run. Fails when no project is open, and at the
    /// first error under --fail-on-error.
    /// </summary>
    internal Result Run(ServiceCollection services, IFileSystem files, AssetManager assets, Events events)
    {
        if (Project is not { } project)
            return Result.Failure("No project is open.");

        assets.AddAssetDirectory(files.GetParent(project.Path));
        Threads threads = new();
        PipelineCoordinator pipelines = new(threads);
        services.AddSingleton(threads);
        GemRegistry registry = new(services, files, project.Gems, assets);

        // Every asset directory is indexed before any gem is made, so a constructor can find assets.
        assets.Index();

        // Never disposed, since its singletons are the gems, which Unload ends.
        services.MakeReadOnly();
        IServiceProvider provider = services.BuildServiceProvider();

        // Core's own join first, so each gem's runs after them.
        Stopwatch clock = new();
        PumpPipeline pump = new(this, files, assets, events, threads);
        pipelines.Get<BeginUpdate>().Add(pump, nameof(PumpPipeline), null);
        PipelineList<CoreState> core = pipelines.Get<CoreState>();
        core.Add(new ClockPipeline(this, clock), nameof(ClockPipeline), null);
        core.Add(new FramePipeline(pipelines), nameof(FramePipeline), null);
        core.Add(new SyncPipeline(clock), nameof(SyncPipeline), null);

        IReadOnlyList<LoadedGem> gems = GemLoader.Load(provider, registry, assets, pipelines);
        Window = GetWindow(provider);

        Log.Info($"Running {project.Name} in {(IsHeadless ? "headless" : "GUI")} mode.");
        IsRunning = true;

        // TODO(KONFUS): pump the GUI like the terminal; until there is one, a run with a window exits at once.
        if (!IsHeadless)
            Exit();

        using CancellationTokenSource stopping = new();
        Task starting = pipelines.Get<Startup>().RunOnMainAsync(new Startup(project), null, stopping.Token).AsTask();
        RunUntil(starting, pump, stopping);

        clock.Start();
        CoreState state = default;
        while (IsRunning && !HasFailed)
        {
            state = core.Run(state, stopping.Token);
        }

        stopping.Cancel();
        Task ending = pipelines
            .Get<Shutdown>()
            .RunOnMainInReverseAsync(new Shutdown(IsExiting), null, default)
            .AsTask();
        RunUntil(ending, pump, stopping);

        bool isFailed = HasFailed;
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

    /// <summary>
    /// Keeps <paramref name="pump"/> going until <paramref name="running"/>, an awaited pipeline outside the frames,
    /// has ended, as what it awaits needs it, cancelling <paramref name="stopping"/> once the run is to end. The
    /// one wait on the main thread: no frame is due meanwhile, and this is what keeps the rest going.
    /// </summary>
    private void RunUntil(Task running, PumpPipeline pump, CancellationTokenSource stopping)
    {
        running.FireAndForget();
        while (!running.IsCompleted)
        {
            pump.Pump();
            if (!IsRunning)
                stopping.Cancel();

            Thread.Sleep(1);
        }
    }
}
