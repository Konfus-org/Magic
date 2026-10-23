using Magic.Contexts.Assets;
using Magic.Extensions;

namespace Magic.Services;

/// <summary>
/// The app that runs: the project it was opened with, its pausing and its quitting. Any gem can pause it or quit it, and
/// Ctrl+C in a headless run quits it; the frame loop finishes the frame it is in and the run ends. One per run, as a
/// restart makes another, unpaused. Safe to use from any thread.
/// </summary>
public sealed class App : IDisposable
{
    private readonly CancellationTokenSource _quitting = new();
    private int _quit;

    /// <summary>
    /// Frames asked for (<see cref="Step"/>) and not yet run.
    /// </summary>
    private int _steps;

    public App(Project project, string folder)
    {
        Project = project;
        Folder = folder;

        // Kept, since a disposed source no longer hands out its token.
        Quitting = _quitting.Token;
    }

    /// <summary>
    /// The project's .magic file, as read.
    /// </summary>
    public Project Project { get; }

    /// <summary>
    /// The folder the project's .magic file is in.
    /// </summary>
    public string Folder { get; }

    /// <summary>
    /// Cancelled once the app starts quitting: for work that would otherwise outlive it.
    /// </summary>
    public CancellationToken Quitting { get; }

    public bool IsQuitting => Quitting.IsCancellationRequested;

    /// <summary>
    /// Whether the app is paused, as a debugger pauses it: from the end of the frame running when it is set until it is
    /// set false, no frame runs (<see cref="Step"/> runs one). The frame index and both clocks stand still, the last
    /// image stays shown, and sound holds. The main thread still runs what is handed to it
    /// (<see cref="Threads.PostAsync(Contexts.Threading.EngineThread, Action, CancellationToken)"/>), and each gem
    /// idles (<see cref="Interfaces.IGem.Idle"/>), so the window answers the OS and lines typed in the terminal still
    /// run. The game's own pause, in which the world is drawn and what is unpausable runs, is
    /// <see cref="World.IsPaused"/>. Changing it is logged.
    /// </summary>
    public bool IsPaused
    {
        get => Volatile.Read(ref field);
        set
        {
            if (Volatile.Read(ref field) == value) return;

            Volatile.Write(ref field, value);
            Interlocked.Exchange(ref _steps, 0);
            Debugging.Info(value ? "The app is paused." : "The app goes on.");
        }
    }

    /// <summary>
    /// Whether the app quits to start again at once (<see cref="Restart"/>), every setting and the world as they are
    /// now.
    /// </summary>
    public bool IsRestarting { get; private set; }

    public void Dispose() => _quitting.Dispose();

    /// <summary>
    /// Quits after this frame. <paramref name="reason"/> goes to the log, so the app never quits unexplained; only the
    /// first is logged, since the app is quitting already.
    /// </summary>
    public void Quit(string reason) => End(reason, restart: false);

    /// <summary>
    /// Quits to start again at once, every setting as it is now and the world as it is, saved as the last frame ends
    /// and put back on the first (<see cref="World.SaveAsync"/>, <see cref="World.Restore"/>), so the game goes on where
    /// it was: for a setting read only as the engine starts, or for whatever else wants the gems made again. Nothing,
    /// when the app is quitting already.
    /// </summary>
    public void Restart(string reason) => End(reason, restart: true);

    /// <summary>
    /// Runs one more frame while the app is paused, then holds again: each call one frame, its clocks moved on by one
    /// fixed step. Nothing when the app is not paused.
    /// </summary>
    public void Step()
    {
        if (IsPaused) Interlocked.Increment(ref _steps);
    }

    /// <summary>
    /// Whether a frame asked for (<see cref="Step"/>) is there to run, taking it when it is. Main thread, as the frame
    /// loop holds.
    /// </summary>
    internal bool TryTakeStep()
    {
        int steps = Volatile.Read(ref _steps);
        while (steps > 0)
        {
            int seen = Interlocked.CompareExchange(ref _steps, steps - 1, steps);

            if (seen == steps) return true;

            steps = seen;
        }

        return false;
    }

    private void End(string reason, bool restart)
    {
        if (Interlocked.Exchange(ref _quit, 1) == 1) return;

        IsRestarting = restart;
        Debugging.Info($"{(restart ? "Restarting" : "Quitting")}: {reason}");

        // Callbacks on the token run on a worker, so what they do or throw never lands on the thread that quit.
        _quitting.CancelAsync().FireAndForget();
    }
}
