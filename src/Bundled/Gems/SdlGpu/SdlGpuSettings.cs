using System.ComponentModel;

namespace Magic.SdlGpu;

/// <summary>
/// The GPU device SdlGpu opens. Set in the project's <c>Presets/Default.preset</c>, by another preset, or with
/// <c>--set SdlGpu.driver=vulkan</c>.
/// </summary>
[Browsable(false)]
internal sealed class SdlGpuSettings
{
#if RELEASE
    private const bool IsRelease = true;
#else
    private const bool IsRelease = false;
#endif

    /// <summary>
    /// The graphics API SDL drives: <c>vulkan</c>, <c>direct3d12</c> or <c>metal</c>; empty for SDL's choice. The
    /// <c>SDL_GPU_DRIVER</c> environment variable still wins.
    /// </summary>
    public string Driver { get; set; } = string.Empty;

    /// <summary>
    /// Open the GPU that saves power, the integrated one on a machine with two, rather than the fastest. SDL picks
    /// between GPUs only this way, never by name; the log names the one it opened.
    /// </summary>
    public bool LowPower { get; set; }

    /// <summary>
    /// Present on the display's refresh. Off, a frame is shown as soon as it is done, tearing where the OS allows it. On
    /// by default only in a release: a Debug or Optimize build runs uncapped, so it shows its true frame rate.
    /// </summary>
    public bool Vsync { get; set; } = IsRelease;

    /// <summary>
    /// The API's own validation: slow, and loud about misuse. It needs the API's validation layers installed.
    /// </summary>
    public bool Validation { get; set; }
}
