using System.ComponentModel;
using System.Drawing;
using Magic.Configuration;

namespace Magic.SdlPlatform;

/// <summary>
/// The window's and the input's settings.
/// </summary>
[Category("Window")]
public sealed record SdlPlatformSettings
{
    /// <summary>
    /// How the window shows: fullscreen fills the display without a border, maximized fills it as a window, and windowed
    /// is a window of its last size.
    /// </summary>
#if DEBUG
    public WindowMode Mode { get; init; } = WindowMode.Maximized;
#else
    public WindowMode Mode { get; init; } = WindowMode.Fullscreen;
#endif

    /// <summary>
    /// The window's size in pixels when it is windowed, until it is resized.
    /// </summary>
    [RequireRestart]
    public Size WindowedSize { get; init; } = new(1280, 720);

    /// <summary>
    /// How far a gamepad's stick moves from its centre before it counts, as a part of its full deflection.
    /// </summary>
    [Category("Input")]
    public float StickDeadZone { get; init; } = 0.15f;

    /// <summary>
    /// How far a control moves before it counts as pressed, as a part of its full travel: a stick or trigger part of
    /// the way, a key or button all of it.
    /// </summary>
    [Category("Input")]
    public float PressThreshold { get; init; } = 0.5f;
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
