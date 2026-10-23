using System.Diagnostics.Metrics;

namespace Magic.Contexts.Telemetry;

/// <summary>
/// The names of what Core measures and tags it with (Core's README, Telemetry), so a listener that singles one out
/// compares against the name Core records under rather than a copy of it; the meter (<c>Magic.Core</c>) Core's own
/// parts record on; and the bucket boundaries histograms of time advise, as exporters' own are made for milliseconds
/// and would put everything in the first.
/// </summary>
public static class Metrics
{
    /// <summary>
    /// Time from one frame's start to the next's, on the main thread.
    /// </summary>
    public const string FrameDuration = "magic.core.frame.duration";

    /// <summary>
    /// How long the main thread waited for the render thread before <c>Sync</c>.
    /// </summary>
    public const string RenderWaitDuration = "magic.core.frame.render_wait.duration";

    /// <summary>
    /// How long one gem's hook took, tagged with <see cref="GemNameTag"/> and <see cref="FramePhaseTag"/>.
    /// </summary>
    public const string HookDuration = "magic.core.gem.hook.duration";

    /// <summary>
    /// How long one piece of work handed to an engine thread took, tagged with <see cref="ThreadNameTag"/>.
    /// </summary>
    public const string WorkDuration = "magic.core.thread.work.duration";

    /// <summary>
    /// How long an asset took to read, make and post-process, tagged with <see cref="AssetTypeTag"/>.
    /// </summary>
    public const string AssetLoadDuration = "magic.core.asset.load.duration";

    /// <summary>
    /// The gem a hook belongs to, named as the project lists it.
    /// </summary>
    public const string GemNameTag = "magic.core.gem.name";

    /// <summary>
    /// The phase a hook ran in, named as the hook is (<c>Update</c>, <c>Render</c>).
    /// </summary>
    public const string FramePhaseTag = "magic.core.frame.phase";

    /// <summary>
    /// The engine thread a piece of work ran on: <c>main</c> or <c>render</c>.
    /// </summary>
    public const string ThreadNameTag = "magic.core.thread.name";

    /// <summary>
    /// The type of the asset loaded, by its type's name.
    /// </summary>
    public const string AssetTypeTag = "magic.core.asset.type";

    /// <summary>
    /// For what happens within a frame: a tenth of a millisecond to a second, with edges at the frame budgets of 240,
    /// 144, 120, 60 and 30 frames a second.
    /// </summary>
    public static InstrumentAdvice<double> FrameBuckets { get; } = new()
    {
        HistogramBucketBoundaries =
        [
            0.0001, 0.00025, 0.0005, 0.001, 0.002,
            1.0 / 240, 1.0 / 144, 1.0 / 120, 1.0 / 60, 1.0 / 30,
            0.05, 0.1, 0.25, 0.5, 1,
        ],
    };

    internal static Meter Meter { get; } = new("Magic.Core");

    /// <summary>
    /// For work that spans frames, such as a load: a millisecond to ten seconds.
    /// </summary>
    internal static InstrumentAdvice<double> WorkBuckets { get; } = new()
    {
        HistogramBucketBoundaries = [0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10],
    };
}
