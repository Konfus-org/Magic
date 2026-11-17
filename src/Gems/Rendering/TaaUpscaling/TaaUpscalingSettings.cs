using System.ComponentModel;
using Magic.Configuration;

namespace Magic.TaaUpscaling;

/// <summary>
/// How the upscaled picture is finished.
/// </summary>
[Category("Rendering")]
public sealed class TaaUpscalingSettings : ISettings
{
    /// <summary>
    /// How much the upscaled picture is sharpened, from 0, which turns it off, to 1.
    /// </summary>
    public float Sharpness { get; set; } = 0.45f;
}
