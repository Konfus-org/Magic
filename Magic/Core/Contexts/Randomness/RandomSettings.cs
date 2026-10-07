using System.ComponentModel;

namespace Magic.Contexts.Settings;

/// <summary>
/// The one number every random thing starts from, through <see cref="Services.Randomness"/>. Read whenever a random
/// sequence is made, so setting it while running changes what is made from then on.
/// </summary>
[Settings("Random")]
[Category("General/Random")]
public sealed class RandomSettings
{
    /// <summary>
    /// 0, the default, is unset: each start rolls its own (logged as <c>Random seed N</c>). Any other value repeats
    /// a run exactly, say to debug what one seed made.
    /// </summary>
    [DisplayName("Random seed")]
    [Description("0 rolls a new seed each start (the log names it); any other value repeats that run's randomness.")]
    public int Seed { get; set; }
}
