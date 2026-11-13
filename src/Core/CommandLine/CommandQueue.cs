using System.Diagnostics.CodeAnalysis;
using Magic.Debugging;
using Magic.Events;

namespace Magic.CommandLine;

/// <summary>
/// The lines that the console runs next, and what holds them back.
/// </summary>
/// <remarks>
/// Lines that a command queues run before the lines of the terminal, in queue order. A hold keeps back each line after
/// the line that set it, until its condition says no. Use it on the game thread.
/// </remarks>
internal sealed class CommandQueue(EventCoordinator events)
{
    private readonly Queue<string> _queued = new();

    /// <summary>
    /// The last change to the world a command started; none before the first.
    /// </summary>
    private Task? _change;

    private Func<bool>? _hold;

    /// <summary>
    /// Whether the first frame has been drawn, as the frame's messages said by the last <see cref="TryDequeue"/>.
    /// </summary>
    public bool HasDrawn { get; private set; }

    /// <summary>
    /// Whether a change to the world a command started is still on its way.
    /// </summary>
    public bool IsChangingWorld => _change is { IsCompleted: false };

    /// <summary>
    /// Runs <paramref name="line"/> after the lines queued before it, ahead of any typed into the terminal.
    /// </summary>
    public void Enqueue(string line) => _queued.Enqueue(line);

    /// <summary>
    /// Holds the lines after this one while <paramref name="isHeld"/> says so, asked once per <see cref="TryDequeue"/>.
    /// </summary>
    public void Hold(Func<bool> isHeld) => _hold = isHeld;

    /// <summary>
    /// Returns <paramref name="change"/>, a change to the world a command started: <see cref="IsChangingWorld"/> holds
    /// until it ends.
    /// </summary>
    public Task<T> ChangeWorldAsync<T>(Task<T> change)
    {
        _change = change;

        return change;
    }

    /// <summary>
    /// Gets the next line to run when no hold keeps it back: a queued line, else a line typed into the terminal.
    /// </summary>
    /// <remarks>
    /// It returns false while a hold keeps the line, or when no line waits. Call it once a frame, after the messages of
    /// the frame were taken.
    /// </remarks>
    public bool TryDequeue([NotNullWhen(true)] out string? line)
    {
        line = null;
        HasDrawn |= !events.GetTaken<FirstFrameDrawn>().IsEmpty;
        if (_hold?.Invoke() == true)
            return false;

        _hold = null;

        return _queued.TryDequeue(out line) || Terminal.Typed.TryRead(out line);
    }
}
