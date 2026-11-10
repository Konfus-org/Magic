using System.ComponentModel;

namespace Magic.VulkanGraphics;

/// <summary>
/// How frames reach the window.
/// </summary>
[Category("Window")]
public sealed record VulkanGraphicsSettings
{
    /// <summary>
    /// Whether a frame waits for the display to show it, which caps the frame rate at the display's and never tears.
    /// Off, frames show as fast as they are drawn: without tearing where the display allows it, else torn.
    /// </summary>
    public bool IsVSyncOn { get; init; }
}
