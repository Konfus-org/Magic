using System.ComponentModel;

namespace Magic.Contexts.Settings;

/// <summary>
/// The world at start: <c>--entry-point</c> sets <see cref="EntryPoint"/>.
/// </summary>
[Settings("World")]
[Browsable(false)] // read once at start: no knob for the settings window
public sealed class WorldSettings
{
    /// <summary>
    /// The domain opened at start-up, as a path among the assets (a gem's or the project's) (<c>Cave/Cave.domain</c>), in place of the
    /// project's <c>"entryPoint"</c>; empty takes the project's.
    /// </summary>
    public string EntryPoint { get; set; } = "";
}
