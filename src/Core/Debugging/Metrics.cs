using System.Diagnostics.Metrics;

namespace Magic.Debugging;

/// <summary>
/// Core's meter, <c>Magic.Core</c>, which Core's parts record on for whatever listens, and the keys of the tags they
/// record with. Instruments are named <c>magic.core.*</c>, and their time is in seconds.
/// </summary>
internal static class Metrics
{
    /// <summary>
    /// The gem a hook belongs to, named as the project lists it.
    /// </summary>
    public const string GemNameTag = "magic.core.gem.name";

    /// <summary>
    /// The hook a gem ran: <c>Update</c>, <c>Render</c>.
    /// </summary>
    public const string HookTag = "magic.core.gem.hook";

    /// <summary>
    /// A step of Core's own in the run's loop.
    /// </summary>
    public const string StepTag = "magic.core.step";

    public static Meter Meter { get; } = new("Magic.Core");
}
