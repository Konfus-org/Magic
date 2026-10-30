namespace Magic.Runtime;

/// <summary>
/// The run: which project it runs and how, and whether it goes on. Commands change it, at launch and while it runs;
/// the launcher's loop acts on it.
/// </summary>
public sealed class App
{
    /// <summary>
    /// The project to run, as read from its <c>.magic</c> file; none until one is opened. Opening another ends the run,
    /// and the launcher runs the new project next; opening the same file again changes nothing.
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
    /// Whether the run has no window. Changing it ends the run, and the launcher runs it again in the new mode.
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
    /// Whether the current run goes on: <see cref="Start"/> begins it; <see cref="Exit"/>, or a new project or mode,
    /// ends it.
    /// </summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    /// Whether the process ends once the run does.
    /// </summary>
    public bool IsExiting { get; private set; }

    /// <summary>
    /// Begins a run of the current project and mode.
    /// </summary>
    public void Start() => IsRunning = true;

    public void Exit()
    {
        IsExiting = true;
        IsRunning = false;
    }
}
