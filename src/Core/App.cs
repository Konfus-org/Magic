using Magic.Debugging;
using Magic.Platform;
using Magic.Projects;
using Magic.Threading;
using Magic.Utils;

namespace Magic;

/// <summary>
/// The app: which project it runs and how, and whether it goes on. Commands change it, at launch and while it runs. It lasts
/// the whole process, so what they set stays set across runs.
/// </summary>
public sealed class App : IDisposable
{
    /// <summary>
    /// Cancelled once the process is to end: by <see cref="Exit"/>, or by an error under --fail-on-error. Every run's
    /// <see cref="Lifetime"/> is linked to it.
    /// </summary>
    private readonly CancellationTokenSource _exiting = new();

    private readonly Subscription _logging;

    private CancellationTokenSource _lifetime;

    internal App()
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(_exiting.Token);
        _logging = Log.Register(new FailOnError(this));
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
            Restart();
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
            Restart();
        }
    }

    /// <summary>
    /// Whether the process exits with 2 as soon as an error is logged, or at once if one already was.
    /// </summary>
    public bool ShouldExitOnError
    {
        get;
        set
        {
            field = value;
            if (HasFailed)
                Fail();
        }
    }

    /// <summary>
    /// Pauses the application, simulation systems will not be run until this is set back to true.
    /// Should not be used for a gameplay pause.
    /// </summary>
    public bool IsPaused { get; set; }

    /// <summary>
    /// Whether the app runs its project: from the command that opens one until <see cref="Exit"/>, across restarts.
    /// </summary>
    public bool IsRunning => Project is not null && !IsExiting;

    /// <summary>
    /// Whether the process ends once the run does.
    /// </summary>
    public bool IsExiting => _exiting.IsCancellationRequested;

    /// <summary>
    /// The run's token, cancelled when the run ends: on <see cref="Exit"/>, a restart, a new project, a new mode or an
    /// error under --fail-on-error. Work that belongs to the run stops with it.
    /// </summary>
    public CancellationToken Lifetime => _lifetime.Token;

    /// <summary>
    /// Whether --fail-on-error has seen its error.
    /// </summary>
    internal bool HasFailed => ShouldExitOnError && Log.ErrorCount > 0;

    public void Dispose()
    {
        _logging.Dispose();
        _lifetime.Dispose();
        _exiting.Dispose();
    }

    public void Exit() => _exiting.Cancel();

    /// <summary>
    /// Ends the run, and Magic runs the project again with everything made anew.
    /// </summary>
    internal void Restart() => _lifetime.Cancel();

    /// <summary>
    /// Starts a run with a new <see cref="Lifetime"/>, already cancelled if the process is exiting.
    /// </summary>
    internal void StartRun()
    {
        _lifetime.Dispose();
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(_exiting.Token);
    }

    /// <summary>
    /// Exits for --fail-on-error's error. It comes from whichever thread logged it, inside the log's lock, so what waits
    /// on the run runs on a worker, not there.
    /// </summary>
    private void Fail() => _exiting.CancelAsync().FireAndForget();

    /// <summary>
    /// Fails the app at the first error logged under --fail-on-error.
    /// </summary>
    private sealed class FailOnError(App app) : ILogSink
    {
        public void Write(in Log.Line line)
        {
            if (line.Level >= Log.LoggingLevel.Error && app.ShouldExitOnError)
                app.Fail();
        }

        public void Flush() { }
    }
}
