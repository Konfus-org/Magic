namespace Magic.Host;

/// <summary>
/// The run: which project it runs and how, and whether it goes on. Commands change it, at launch and while it runs;
/// the launcher's loop acts on it.
/// </summary>
public sealed class App
{
    /// <summary>
    /// The .magic file to run; empty until a project is given. Changing it ends the run, and the launcher runs the
    /// new project next.
    /// </summary>
    public string ProjectPath
    {
        get;
        set
        {
            if (value == field)
                return;

            field = value;
            IsRunning = false;
        }
    } = "";

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
    public bool FailOnError { get; set; }

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
