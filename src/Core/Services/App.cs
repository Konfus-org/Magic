using Magic.Contexts.Assets;
using Magic.Extensions;

namespace Magic.Services;

/// <summary>
/// The app that runs: the project it was opened with, and its quitting. Any gem can quit it, and so can Ctrl+C in a
/// headless run; the frame loop finishes the frame it is in and the run ends. One per run, as a restart makes another.
/// Safe to use from any thread.
/// </summary>
public sealed class App : IDisposable
{
    private readonly CancellationTokenSource _quitting = new();
    private int _quit;

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

    private void End(string reason, bool restart)
    {
        if (Interlocked.Exchange(ref _quit, 1) == 1) return;

        IsRestarting = restart;
        Debugging.Info($"{(restart ? "Restarting" : "Quitting")}: {reason}");

        // Callbacks on the token run on a worker, so what they do or throw never lands on the thread that quit.
        _quitting.CancelAsync().FireAndForget();
    }
}
