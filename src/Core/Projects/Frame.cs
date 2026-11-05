using Magic.Events;

namespace Magic.Projects;

/// <summary>
/// One frame's clocks and messages, handed to every hook the frame runs.
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
    /// The messages published during the last frame. None in a fixed step, which takes what it needs from what
    /// <see cref="IGem.Update"/> read.
    /// </summary>
    public FrameEvents Events { get; init; }

    /// <summary>
    /// How long the main thread worked on the last frame; all zero in the first.
    /// </summary>
    public CpuTimings Cpu { get; init; }
}
