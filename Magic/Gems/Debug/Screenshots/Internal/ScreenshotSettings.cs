using Magic.Contexts.Settings;
using System.ComponentModel;

namespace ScreenshotsGem;

/// <summary>
/// Screenshots taken on their own, by frame: <c>--screenshots</c>, <c>--screenshot-delay</c> and
/// <c>--screenshot-interval</c> set these. A screenshot that was asked for and not taken is logged as an error.
/// </summary>
[Settings("Screenshots")]
[Browsable(false)] // read once at start: no knob for the settings window
internal sealed class ScreenshotSettings
{
    /// <summary>
    /// How many to take; 0 takes none.
    /// </summary>
    public int Count { get; set; }

    /// <summary>
    /// The frame the first is taken at the end of.
    /// </summary>
    public long Delay { get; set; } = 1;

    /// <summary>
    /// Frames between one and the next.
    /// </summary>
    public long Interval { get; set; } = 60;
}
