using System.ComponentModel;
using System.Drawing;
using Magic.Configuration;

namespace Magic.SdlPlatform;

/// <summary>
/// The window's settings with the frames' pace and the pause while it has no focus, and the input's settings.
/// </summary>
[Category("Window")]
public sealed class SdlPlatformSettings : ISettings
{
    /// <summary>
    /// How the window shows: fullscreen fills the display without a border, maximized fills it as a window, and windowed
    /// is a window of its last size.
    /// </summary>
#if DEBUG
    public WindowMode Mode { get; set; } = WindowMode.Maximized;
#else
    public WindowMode Mode { get; set; } = WindowMode.Fullscreen;
#endif

    /// <summary>
    /// The window's size in pixels when it is windowed, until it is resized.
    /// </summary>
    [RequireRestart]
    public Size WindowedSize { get; set; } = new(1280, 720);

    /// <summary>
    /// Whether the window's edges can be dragged to resize it when it is windowed.
    /// </summary>
    public bool IsResizable { get; set; } = true;

    /// <summary>
    /// The display the window shows on, by its place in the system's list of displays; the first is usually the main
    /// one.
    /// </summary>
    public int Display { get; set; }

    /// <summary>
    /// The most frames a second while the window has the focus; 0 for no cap. Vsync caps them at the display's rate too.
    /// </summary>
    public int MaxFrameRate { get; set; }

    /// <summary>
    /// The most frames a second while the window doesn't have the focus, to spare the machine; 0 for no cap.
    /// </summary>
    public int BackgroundFrameRate { get; set; }

    /// <summary>
    /// Whether the world pauses while the window doesn't have the focus, and goes on as it gets it back.
    /// </summary>
    [Category("Simulation")]
    public bool PauseWhenUnfocused { get; set; }

    /// <summary>
    /// How many times the mouse's motion is multiplied: 2 moves twice as far.
    /// </summary>
    [Category("Input")]
    public float MouseSensitivity { get; set; } = 1;

    /// <summary>
    /// Seconds over which the mouse's motion eases in, which steadies a jittery mouse and makes it lag; 0 is raw.
    /// </summary>
    [Category("Input")]
    public float MouseSmoothing { get; set; }

    /// <summary>
    /// How far a gamepad's stick moves from its centre before it counts, as a part of its full deflection.
    /// </summary>
    [Category("Input")]
    public float StickDeadZone { get; set; } = 0.15f;

    /// <summary>
    /// The power a stick's deflection past its dead zone is raised to: 1 is linear, and more gives finer control near
    /// the centre and the same speed at full deflection.
    /// </summary>
    [Category("Input")]
    public float StickCurve { get; set; } = 1;

    /// <summary>
    /// How far a control moves before it counts as pressed, as a part of its full travel: a stick or trigger part of
    /// the way, a key or button all of it.
    /// </summary>
    [Category("Input")]
    public float PressThreshold { get; set; } = 0.5f;
}

/// <summary>
/// How the window shows.
/// </summary>
public enum WindowMode
{
    Fullscreen,
    Maximized,
    Windowed,
}
