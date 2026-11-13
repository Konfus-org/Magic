using Magic.Events;

namespace Magic.Projects;

/// <summary>
/// One frame's clocks and messages, given to each hook that the frame runs.
/// </summary>
public readonly record struct Frame
{
    public required uint Index { get; init; }

    /// <summary>
    /// Seconds since the run started. Keeps counting while paused, for what still updates then.
    /// </summary>
    public required double AppSec { get; init; }

    /// <summary>
    /// Seconds since the last frame. Keeps counting while paused.
    /// </summary>
    public required double AppDeltaSec { get; init; }

    /// <summary>
    /// Seconds the simulation has run. Stops while paused.
    /// </summary>
    public required double SimSec { get; init; }

    /// <summary>
    /// Seconds the simulation moved since the last frame, or the last fixed step in a fixed step. Zero while paused.
    /// </summary>
    public required double SimDeltaSec { get; init; }

    public bool IsPaused { get; init; }

    /// <summary>
    /// The messages published during the last frame. None in a fixed step: take them in <see cref="IGem.Update"/>.
    /// </summary>
    public FrameEvents Events { get; init; }
}
