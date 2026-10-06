using Magic.Contexts.Settings;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace SDLRenderGem;

/// <summary>
/// Which GPU API SDL drives: its choice, or one asked for.
/// </summary>
public enum GpuBackend : byte
{
    Auto,
    Vulkan,

    [JsonStringEnumMemberName("direct3d12")]
    Direct3D12
}

/// <summary>
/// The GPU device: which backend SDL opens and how it presents. Both change live: another backend makes the device
/// again between two frames.
/// </summary>
[Settings("Gpu")]
[Category("Display/Window & GPU")]
public sealed class GpuSettings
{
    /// <summary>
    /// The GPU API. The <c>SDL_GPU_DRIVER</c> environment variable still wins.
    /// </summary>
    [Description("The GPU API: SDL's choice, Vulkan or Direct3D 12. Changing it makes the device again, which takes a few frames.")]
    public GpuBackend Backend { get; set; }

    /// <summary>
    /// Present on the display's refresh. On by default, except in a Debug build, where frame times are worth more uncapped.
    /// </summary>
    [DisplayName("Vertical sync")]
    [Description("Present on the display's refresh. Off in a Debug build by default, so frame times are uncapped.")]
#if DEBUG
    public bool Vsync { get; set; }
#else
    public bool Vsync { get; set; } = true;
#endif
}
