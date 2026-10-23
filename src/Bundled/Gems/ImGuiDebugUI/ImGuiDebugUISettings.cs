using System.ComponentModel;
using Magic.Contexts.Assets;
using Magic.Interfaces;

namespace Magic.ImGuiDebugUI;

/// <summary>
/// How the debug UI is written and how long what is reported stays. Set in the project's
/// <c>Presets/Default.preset</c>, by another preset, or with <c>--set ImGuiDebugUI.fontSize=24</c>.
/// </summary>
[Category("Debug")]
internal sealed class ImGuiDebugUISettings : ISettings
{
    /// <summary>
    /// The font everything is written in; none for the default font. Changed while running, the UI is written in the
    /// new one once it loads.
    /// </summary>
    public Handle<Font> Font { get; set; }

    /// <summary>
    /// The height of a line of text in window pixels, which every widget is sized around. Changed while running, the
    /// UI is drawn at the new size, its glyphs stretched from the font's own.
    /// </summary>
    public float FontSize { get; set; } = 20;

    /// <summary>
    /// Seconds a line on screen, or a warning or error in the world, stays when its report does not say.
    /// </summary>
    public float ReportSec { get; set; } = 5;

    /// <summary>
    /// Metres from a camera beyond which text in the world is not shown in its view.
    /// </summary>
    public float TextDistance { get; set; } = 50;
}
