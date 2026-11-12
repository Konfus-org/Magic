using System.ComponentModel;
using Magic.Configuration;

namespace Magic.VulkanGraphics;

/// <summary>
/// How frames reach the window, and the GPU that draws them.
/// </summary>
[Category("Window")]
public sealed class VulkanGraphicsSettings : ISettings
{
    /// <summary>
    /// Whether a frame waits for the display to show it, which caps the frame rate at the display's and never tears.
    /// Off, frames show as fast as they are drawn, and can tear.
    /// </summary>
    public bool IsVSyncOn { get; set; }

    /// <summary>
    /// The GPU that draws, by a part of its name as the log gives it, such as RTX or Intel; empty for the fastest. A
    /// name no GPU that can draw has leaves the fastest, with a warning.
    /// </summary>
    [Category("Rendering")]
    [RequireRestart]
    public string Gpu { get; set; } = string.Empty;
}
