using System.ComponentModel;
using Magic.Configuration;

namespace Magic.ImGuiDebugUI;

/// <summary>
/// How the debug UI's text and windows look, and what of the log shows on screen.
/// </summary>
[Category("Debug")]
public sealed class ImGuiDebugUISettings : ISettings
{
    /// <summary>
    /// Whether the lines logged to show on screen show at all times, or only while a debug window is open.
    /// </summary>
#if DEBUG
    public bool IsLogAlwaysShown { get; set; } = true;
#else
    public bool IsLogAlwaysShown { get; set; }
#endif

    /// <summary>
    /// Seconds a line logged to show on screen stays.
    /// </summary>
    public float ReportSec { get; set; } = 5;

    /// <summary>
    /// Metres from the camera beyond which text in the world is not shown.
    /// </summary>
    public float TextDistance { get; set; } = 50;

    /// <summary>
    /// How dark the plate behind text in the world and the lines on screen is, from 0, clear, to 1, black.
    /// </summary>
    public float OverlayOpacity { get; set; } = 0.75f;

    /// <summary>
    /// The height of a line of text before the UI's scale, which every widget is sized around.
    /// </summary>
    public float FontSize { get; set; } = 20;

    /// <summary>
    /// How many times bigger the debug UI draws, its text and its spacing: 2 for twice. 0 takes the display's own scale.
    /// </summary>
    public float UiScale { get; set; }
}
