using System.ComponentModel;

namespace Magic.SdlPlatform;

/// <summary>
/// The window SdlPlatform opens. Set in the project's <c>Presets/Default.preset</c>, by another preset, or with
/// <c>--set SdlPlatform.width=...</c>. Sizes are in screen points, which are pixels at 100% display scaling.
/// </summary>
[Browsable(false)]
internal sealed class SdlPlatformSettings
{
    public int Width { get; set; } = 1280;

    public int Height { get; set; } = 720;
}
