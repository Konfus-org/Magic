using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Magic.Interfaces;

namespace Magic.SdlPlatform;

/// <summary>
/// The window SdlPlatform opens, and how it reads the mouse and gamepads. Set in the project's
/// <c>Presets/Default.preset</c>, by another preset, or with <c>--set SdlPlatform.width=...</c>. Sizes are in screen
/// points, which are pixels at 100% display scaling. Changed while running, the window is resized; dragging the window
/// changes it and leaves them as they were. The input settings take from the next frame.
/// </summary>
[Category("Window")]
internal sealed class SdlPlatformSettings : ISettings
{
    public int Width { get; set; } = 1280;

    public int Height { get; set; } = 720;

    /// <summary>
    /// What the mouse's motion is multiplied by: above 1 turns faster, 0.1 to 10.
    /// </summary>
    [Category("Input")]
    [Range(0.1, 10)]
    public float MouseSensitivity { get; set; } = 1;

    /// <summary>
    /// Mouse motion up reads as down, and down as up.
    /// </summary>
    [Category("Input")]
    public bool InvertMouseY { get; set; }

    /// <summary>
    /// How far a stick or trigger is pushed before it reads as anything but 0, 0 to 0.5 of its reach; past it, what is
    /// read grows from 0 to 1 at full deflection, so the zone's edge is no step.
    /// </summary>
    [Category("Input")]
    [Range(0, 0.5)]
    public float StickDeadZone { get; set; } = 0.15f;

    /// <summary>
    /// Pushing a stick up reads as down, and down as up.
    /// </summary>
    [Category("Input")]
    public bool InvertStickY { get; set; }
}
