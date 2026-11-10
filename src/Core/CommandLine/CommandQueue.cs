using System.Diagnostics.CodeAnalysis;
using Magic.Debugging;
using Magic.Events;

namespace Magic.CommandLine;

/// <summary>
/// The lines the console runs next, and what holds them back. Lines a command queues run before the terminal's, in
/// the order queued. A hold keeps every line after the one that set it back until its condition says no. Game thread.
/// </summary>
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
    /// Runs <paramref name="change"/>, a change to the world, and returns what it returns: <see cref="IsChangingWorld"/>
    /// holds until it ends.
    /// </summary>
    public Task<T> ChangeWorldAsync<T>(Func<Task<T>> change)
    {
        Task<T> changing = change();
        _change = changing;

        return changing;
    }

    /// <summary>
    /// The next line to run, once no hold keeps it back: a queued one, else one typed into the terminal. False while a
    /// hold keeps it, or when no line is waiting. Once a frame, after the frame's messages were taken.
    /// </summary>
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
