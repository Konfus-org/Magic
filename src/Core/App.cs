using Magic.Debugging;
using Magic.Platform;
using Magic.Projects;

namespace Magic;

/// <summary>
/// The app: which project it runs and how, and whether it goes on. Commands change it, at launch and while it runs. It lasts
/// the whole process, so what they set stays set across runs.
/// </summary>
public sealed class App
{
    internal App() { }

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
    /// Counts what ends a run to be run again, with everything made anew: <see cref="Restart"/>, a new project or a new mode.
    /// A run goes on while it stays as it was when the run began.
    /// </summary>
    internal int RunVersion { get; private set; }

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
    internal void Restart() => RunVersion++;
}
