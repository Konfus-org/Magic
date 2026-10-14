using System.ComponentModel;
using Magic.Contexts.Assets;

namespace Magic.ImGuiDebugUI;

/// <summary>
/// How the debug UI is written and how long what is reported stays. Set in the project's
/// <c>Presets/Default.preset</c>, by another preset, or with <c>--set ImGuiDebugUI.fontSize=24</c>.
/// </summary>
internal sealed class ImGuiDebugUISettings
{
    /// <summary>
    /// The font everything is written in; none for the default font.
    /// </summary>
    [Browsable(false)]
    public Handle<Font> Font { get; set; }

    /// <summary>
    /// The height of a line of text in window pixels, which every widget is sized around.
    /// </summary>
    [Browsable(false)]
    public float FontSize { get; set; } = 20;

    /// <summary>
    /// Seconds a line on screen, or a warning or error in the world, stays when its report does not say.
    /// </summary>
    public float ReportSeconds { get; set; } = 5;

    /// <summary>
    /// Metres from a camera beyond which text in the world is not shown in its view.
    /// </summary>
    public float TextDistance { get; set; } = 50;
}
