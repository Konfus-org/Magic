using Magic.GemAPI;

namespace Magic.Pipelines;

/// <summary>
/// The state of the core pipeline, which is the frame loop: this frame's clocks and what the clocks carry from frame to
/// frame. Core's own run a frame over it; a gem's that joins it runs after the frame.
/// </summary>
public readonly record struct CoreState
{
    public Frame Frame { get; init; }

    /// <summary>
    /// How many fixed steps this frame owes, and so how many times <see cref="FixedUpdate"/> runs.
    /// </summary>
    public int FixedSteps { get; init; }

    internal uint NextIndex { get; init; }

    internal double LastSec { get; init; }

    /// <summary>
    /// Seconds the simulation has run.
    /// </summary>
    internal double SimSec { get; init; }

    /// <summary>
    /// Seconds of fixed steps that ran before this frame's.
    /// </summary>
    internal double FixedSec { get; init; }

    internal double FixedOwedSec { get; init; }

    internal double NextFrameSec { get; init; }
}
