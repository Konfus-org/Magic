using Magic.Contexts.Settings;
using Magic.Interfaces;
using System.ComponentModel;

namespace SdlPlatformGem;

/// <summary>
/// The main window, read once as the gem loads: <c>--headless</c>, <c>--width</c>, <c>--height</c> and
/// <c>--window-mode</c> set these.
/// </summary>
[Settings("Window")]
[Browsable(false)] // read once at start: no knob for the settings window
internal sealed class WindowSettings
{
    /// <summary>
    /// False runs headless: no main window, gems and systems still run.
    /// </summary>
    public bool Open { get; set; } = true;

    public int Width { get; set; } = 800;

    public int Height { get; set; } = 600;

    public WindowMode Mode { get; set; } = WindowMode.Windowed;
}
