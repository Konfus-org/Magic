using Magic.Contexts.Settings;
using System.ComponentModel;

namespace LoadingScreenGem;

/// <summary>
/// The loading screen: <c>--loading</c> sets <see cref="Domain"/>.
/// </summary>
[Settings("LoadingScreen")]
[Browsable(false)] // read once at start: no knob for the settings window
internal sealed class LoadingScreenSettings
{
    /// <summary>
    /// The domain shown, as a path among the assets (a gem's or the project's), in place of the project's <c>"loading"</c>; empty takes
    /// the project's, and without one the engine's own.
    /// </summary>
    public string Domain { get; set; } = "";
}
