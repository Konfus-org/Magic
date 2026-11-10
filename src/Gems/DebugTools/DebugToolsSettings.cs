using System.ComponentModel;

namespace Magic.DebugTools;

/// <summary>
/// Which of the debug lines and windows over the meters show.
/// </summary>
[Category("Debug")]
public sealed record DebugToolsSettings
{
    /// <summary>
    /// Whether the line of the frame rate, the frame time and the memory shows in the top left corner. F1 turns it on
    /// and off.
    /// </summary>
#if DEBUG
    public bool IsCounterShown { get; init; } = true;
#else
    public bool IsCounterShown { get; init; }
#endif

    /// <summary>
    /// Whether the stats window, every instrument of the meters, is open. F3 opens and closes it.
    /// </summary>
    public bool IsStatsOpen { get; init; }
}
