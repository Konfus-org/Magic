using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Magic.Events;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.CommandLine;

/// <summary>
/// The lines the console runs next, and what holds them back. Lines a command queues run before the terminal's, in
/// the order queued. A wait holds every line after it: for a number of frames or seconds, until the world has loaded,
/// or until the first frame is drawn. A change to the world on its way counts as loading. Main thread.
/// </summary>
internal sealed class CommandQueue : IDisposable
{
    private readonly World _world;

    private readonly Queue<string> _queued = new();

    private readonly Subscription _drawnWatch;

    private int _waitFrames;

    /// <summary>
    /// When a wait in seconds ends, as <see cref="Stopwatch.GetTimestamp"/> counts; 0 when none is on.
    /// </summary>
    private long _waitUntilTimestamp;

    private bool _isWaitingForWorld;

    private bool _isWaitingToDraw;

    private bool _hasDrawn;

    private bool _isChangingWorld;

    public CommandQueue(World world, EventCoordinator events)
    {
        _world = world;
        _drawnWatch = events.Watch<FirstFrameDrawn>(_ => _hasDrawn = true);
    }

    public void Dispose() => _drawnWatch.Dispose();

    /// <summary>
    /// Runs <paramref name="line"/> after the lines queued before it, ahead of any typed into the terminal.
    /// </summary>
    public void Enqueue(string line) => _queued.Enqueue(line);

    /// <summary>
    /// Holds the lines after this one for <paramref name="frames"/> frames.
    /// </summary>
    public void WaitFrames(int frames) => _waitFrames = frames;

    /// <summary>
    /// Holds the lines after this one for <paramref name="seconds"/> of real time.
    /// </summary>
    public void WaitSeconds(double seconds) =>
        _waitUntilTimestamp = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);

    /// <summary>
    /// Holds the lines after this one until the world has content, is not loading, and no change to it is on its way.
    /// </summary>
    public void WaitForWorld() => _isWaitingForWorld = true;

    /// <summary>
    /// Holds the lines after this one until the first frame is drawn.
    /// </summary>
    public void WaitForDrawn() => _isWaitingToDraw = true;

    /// <summary>
    /// Runs <paramref name="change"/>, a change to the world, and returns what it returns: a wait for the world holds
    /// until it ends. It counts from this call, before the change's first await.
    /// </summary>
    public async Task<T> ChangeWorldAsync<T>(Func<Task<T>> change)
    {
        _isChangingWorld = true;
        try
        {
            return await change().ConfigureAwait(false);
        }
        finally
        {
            _isChangingWorld = false;
        }
    }

    /// <summary>
    /// The next line to run, once no wait holds it back: a queued one, else one typed into the terminal. False while a
    /// wait holds, or when no line is waiting; a wait for frames counts this call as one.
    /// </summary>
    public bool TryDequeue([NotNullWhen(true)] out string? line)
    {
        line = null;
        if (IsHeld())
            return false;

        return _queued.TryDequeue(out line) || Terminal.Pump(out line);
    }

    /// <summary>
    /// Whether a wait still holds this frame, ending each wait whose time has come; a wait for frames counts one.
    /// </summary>
    private bool IsHeld()
    {
        if (_waitFrames > 0)
        {
            _waitFrames--;

            return true;
        }

        if (_waitUntilTimestamp != 0)
        {
            if (Stopwatch.GetTimestamp() < _waitUntilTimestamp)
                return true;

            _waitUntilTimestamp = 0;
        }

        if (_isWaitingForWorld)
        {
            if (_isChangingWorld || _world.IsLoading || _world.Domains.IsEmpty)
                return true;

            _isWaitingForWorld = false;
        }

        if (_isWaitingToDraw)
        {
            if (!_hasDrawn)
                return true;

            _isWaitingToDraw = false;
        }

        return false;
    }
}
