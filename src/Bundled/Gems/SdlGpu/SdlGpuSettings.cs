using System.ComponentModel;
using Magic.Contexts.Settings;
using Magic.Interfaces;

namespace Magic.SdlGpu;

/// <summary>
/// The GPU device SdlGpu opens. Set in the project's <c>Presets/Default.preset</c>, by another preset, or with
/// <c>--set SdlGpu.driver=vulkan</c>.
/// </summary>
[Category("Render/Device")]
internal sealed class SdlGpuSettings : ISettings
{
#if RELEASE
    private const bool IsRelease = true;
#else
    private const bool IsRelease = false;
#endif

    /// <summary>
    /// The graphics API SDL drives, or <see cref="GpuDriver.Auto"/> for SDL's choice. The <c>SDL_GPU_DRIVER</c>
    /// environment variable still wins. Changed while running, the engine starts again.
    /// </summary>
    [RequireRestart]
    public GpuDriver Driver { get; set; } = GpuDriver.Auto;

    /// <summary>
    /// Open the GPU that saves power, the integrated one on a machine with two, rather than the fastest. SDL picks
    /// between GPUs only this way, never by name; the log names the one it opened. Changed while running, the engine
    /// starts again.
    /// </summary>
    [RequireRestart]
    public bool LowPower { get; set; }

    /// <summary>
    /// Present on the display's refresh. Off, a frame is shown as soon as it is done, tearing where the OS allows it. On
    /// by default only in a release: a Debug or Optimize build runs uncapped, so it shows its true frame rate. Changed
    /// while running, every window presents the new way from the next frame.
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
    /// The API's own validation: slow, and loud about misuse. It needs the API's validation layers installed. Changed
    /// while running, the engine starts again.
    /// </summary>
    [RequireRestart]
    [Category("Debug")]
    public bool Validation { get; set; }
}

/// <summary>
/// The graphics APIs SDL drives, each asked for by SDL's name for it; <see cref="Auto"/> leaves the choice to SDL.
/// </summary>
internal enum GpuDriver : byte
{
    Auto,
    Vulkan,
    Direct3d12,
    Metal,
}

/// <summary>
/// The swapchain compositions SDL offers, each with the image format it implies.
/// </summary>
internal enum Composition : byte
{
    Sdr,
    SdrLinear,
    Hdr10,
    HdrLinear,
}
