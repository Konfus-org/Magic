using Magic.Extensions;

namespace Magic.Services;

/// <summary>
/// Whether the run is ending. Any gem can end it, and so can Ctrl+C in a headless run; the frame loop finishes the
/// frame it is in and stops. Safe to use from any thread.
/// </summary>
public sealed class Lifetime : IDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private int _stopped;

    public Lifetime()
    {
        // Kept, since a disposed source no longer hands out its token.
        Stopping = _stopping.Token;
    }

    public void Dispose()
    {
        _stopping.Dispose();
    }

    public bool IsStopping => Stopping.IsCancellationRequested;

    /// <summary>
    /// Cancelled once the run starts ending: for work that would otherwise outlive it.
    /// </summary>
    public CancellationToken Stopping { get; }

    /// <summary>
    /// Whether the run ends to start again at once (<see cref="Restart"/>), every setting and the world as they are
    /// now.
    /// </summary>
    public bool IsRestarting { get; private set; }

    /// <summary>
    /// Ends the run after this frame. <paramref name="reason"/> goes to the log, so a run never stops unexplained;
    /// only the first is logged, since the run is already ending.
    /// </summary>
    public void Stop(string reason)
    {
        End(reason, restart: false);
    }

    /// <summary>
    /// Ends the run to start it again at once, every setting as it is now and the world as it is, saved as the last
    /// frame ends and put back on the first (<see cref="World.SaveAsync"/>, <see cref="World.RestoreAsync"/>), so the game
    /// goes on where it was: for a setting read only as the engine starts, or for whatever else wants the gems made
    /// again. Nothing, when the run is ending already.
    /// </summary>
    public void Restart(string reason)
    {
        End(reason, restart: true);
    }

    private void End(string reason, bool restart)
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1)
        {
            return;
        }

        IsRestarting = restart;
        Debugging.Info($"{(restart ? "Restarting" : "Stopping")}: {reason}");

        // Callbacks on the token run on a worker, so what they do or throw never lands on the thread that stopped.
        _stopping.CancelAsync().FireAndForget();
    }
}
