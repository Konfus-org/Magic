using System.ComponentModel;
using Magic.Attributes;
using Magic.Interfaces;

namespace Magic.VulkanGpu;

/// <summary>
/// How a window's image is composed onto the display, each with the image format it implies.
/// </summary>
internal enum Composition : byte
{
    Sdr,
    SdrLinear,
    Hdr10,
    HdrLinear,
}

/// <summary>
/// The GPU VulkanGpu opens and how it presents. Set in the project's <c>Presets/Default.preset</c>, by another preset,
/// or with <c>--set VulkanGpu.device=Intel</c>.
/// </summary>
[Category("Render/Device")]
internal sealed class VulkanGpuSettings : ISettings
{
#if RELEASE
    private const bool IsRelease = true;
#else
    private const bool IsRelease = false;
#endif

    /// <summary>
    /// Part of the name of the GPU to open, as the log names it, any case: <c>Intel</c>, <c>3070</c>. Empty, or naming
    /// none, opens the fastest that runs Vulkan 1.3, or the one saving power when <see cref="LowPower"/> says so.
    /// Changed while running, the engine starts again.
    /// </summary>
    [RequireRestart]
    public string Device { get; set; } = string.Empty;

    /// <summary>
    /// Open the GPU that saves power, the integrated one on a machine with two, rather than the fastest, when
    /// <see cref="Device"/> names none. Changed while running, the engine starts again.
    /// </summary>
    [RequireRestart]
    public bool LowPower { get; set; }

    /// <summary>
    /// Present on the display's refresh. Off, a frame is shown as soon as it is done, tearing where the OS allows it.
    /// On by default only in a release: a Debug or Optimize build runs uncapped, so it shows its true frame rate.
    /// Changed while running, every window presents the new way from the next frame.
    /// </summary>
    [Category("Window")]
    public bool Vsync { get; set; } = IsRelease;

    /// <summary>
    /// How a window's image is composed onto the display, which decides its format: <c>sdr</c> (8-bit UNORM, the
    /// frame encodes to sRGB itself), <c>sdr_linear</c> (8-bit sRGB-typed, the GPU encodes), <c>hdr10</c> (10-bit,
    /// ST 2084 on Rec. 2020) or <c>hdr_linear</c> (half floats, linear scRGB). A window that does not support the one
    /// asked for falls back to <c>sdr</c>, logged. Changed while running, every window is composed the new way, its
    /// image in the new format, from the next frame.
    /// </summary>
    public Composition Composition { get; set; } = Composition.Sdr;

    /// <summary>
    /// Vulkan's validation layer: slow, and loud about misuse, each kind of message logged once. It needs the layer
    /// installed, as the Vulkan SDK installs it; without it the GPU opens unvalidated and says so. Changed while
    /// running, the engine starts again.
    /// </summary>
    [RequireRestart]
    [Category("Debug")]
    public bool Validation { get; set; }
}
