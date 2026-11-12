using System.Diagnostics.Metrics;

namespace Magic.Debugging;

/// <summary>
/// Core's meter, <c>Magic.Core</c>, and the names of its instruments and tag keys.
/// </summary>
/// <remarks>
/// Instruments are named <c>magic.core.*</c>, and their time is in seconds.
/// </remarks>
public static class Metrics
{
    public const string FrameDuration = "magic.core.frame.duration";

    public const string FrameWorkDuration = "magic.core.frame.work.duration";

    public const string RenderFrameDuration = "magic.core.render.frame.duration";

    /// <summary>
    /// One gem's hook, tagged with <see cref="GemNameTag"/> and <see cref="HookTag"/>.
    /// </summary>
    public const string HookDuration = "magic.core.gem.hook.duration";

    /// <summary>
    /// One of Core's own steps in the run's loop, tagged with <see cref="StepTag"/>.
    /// </summary>
    public const string StepDuration = "magic.core.step.duration";

    /// <summary>
    /// The gem a hook belongs to, named as the project lists it.
    /// </summary>
    public const string GemNameTag = "magic.core.gem.name";

    /// <summary>
    /// The hook a gem ran, such as <c>Update</c> or <c>Render</c>.
    /// </summary>
    public const string HookTag = "magic.core.gem.hook";

    /// <summary>
    /// One of Core's own steps in the run's loop.
    /// </summary>
    public const string StepTag = "magic.core.step";

    internal static Meter Meter { get; } = new("Magic.Core");

    internal static Histogram<double> HookDurations { get; } =
        Meter.CreateHistogram<double>(HookDuration, "s", "One gem's hook.");
}
