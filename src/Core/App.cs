using Magic.Debugging;
using Magic.Platform;
using Magic.Projects;

namespace Magic;

/// <summary>
/// Holds the project that the process runs, how it runs it, and whether the process goes on.
/// </summary>
/// <remarks>
/// Commands change it at launch and while it runs. It lasts for the whole process, so a value that a command sets stays
/// set across runs.
/// </remarks>
public sealed class App : IDisposable
{
    /// <summary>
    /// Cancelled by <see cref="Exit"/> when the process must end, which also cancels the <see cref="Lifetime"/> of
    /// each run.
    /// </summary>
    private readonly CancellationTokenSource _exiting = new();

    private CancellationTokenSource _lifetime;

    internal App() => _lifetime = CancellationTokenSource.CreateLinkedTokenSource(_exiting.Token);

    /// <summary>
    /// The project to run, as read from its <c>.magic</c> file, or null until a project is opened.
    /// </summary>
    /// <remarks>
    /// If you set a different project, the run ends and Magic runs the new project next. If you set the same file
    /// again, nothing changes.
    /// </remarks>
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
    /// The window that the run draws into, or null when the run is headless.
    /// </summary>
    /// <remarks>
    /// The gem that provides the window owns it. The window is valid only while the run lasts.
    /// </remarks>
    public IWindow? Window { get; internal set; }

    /// <summary>
    /// Whether the run has no window.
    /// </summary>
    /// <remarks>
    /// If you change it, the run ends and Magic runs it again in the new mode.
    /// </remarks>
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
    /// Whether the process exits with 2 once an error is logged.
    /// </summary>
    public bool ShouldExitOnError { get; set; }

    /// <summary>
    /// Whether the process ends once the run does.
    /// </summary>
    public bool IsExiting => _exiting.IsCancellationRequested;

    /// <summary>
    /// A token that is cancelled when the run ends.
    /// </summary>
    /// <remarks>
    /// The run ends on <see cref="Exit"/>, a restart, a new project, a new mode, or an error under
    /// <c>--fail-on-error</c>. Work that belongs to the run must stop with this token.
    /// </remarks>
    public CancellationToken Lifetime => _lifetime.Token;

    /// <summary>
    /// Pumps the OS events on the platform thread while the run lasts. The gem that provides it owns it.
    /// </summary>
    internal IPlatform? Platform { get; set; }

    /// <summary>
    /// Whether an error was logged while <see cref="ShouldExitOnError"/> is set.
    /// </summary>
    internal bool HasFailed => ShouldExitOnError && Log.ErrorCount > 0;

    public void Dispose()
    {
        _lifetime.Dispose();
        _exiting.Dispose();
    }

    public void Exit() => _exiting.Cancel();

    /// <summary>
    /// Ends the run, and Magic then runs the project again from the start.
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
}
