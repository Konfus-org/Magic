using System.ComponentModel;
using Magic.Configuration;

namespace Magic.AmbientOcclusion;

/// <summary>
/// How the ambient occlusion is found.
/// </summary>
[Category("Rendering")]
public sealed class AmbientOcclusionSettings : ISettings
{
    /// <summary>
    /// Whether the ambient occlusion is found; off, all the ambient light reaches every surface.
    /// </summary>
    public bool IsAmbientOcclusionOn { get; set; } = true;

    /// <summary>
    /// How many slices and steps each pixel looks along: more are smoother and cost more.
    /// </summary>
    public AmbientOcclusionQuality Quality { get; set; } = AmbientOcclusionQuality.High;

    /// <summary>
    /// The metres of the sphere around a point in which a surface hides its ambient light, from 0 to 0.68. The search
    /// reaches 1.457 times as far, as XeGTAO's does, so 0.68 reaches 1 m.
    /// </summary>
    public float Radius { get; set; } = 0.5f;

    /// <summary>
    /// Whether the mean direction the ambient light comes from is found, which the ambient light is then read along.
    /// </summary>
    public bool IsBentNormalOn { get; set; } = true;
}

/// <summary>
/// XeGTAO's presets: the slices across the picture and the steps along each side of a slice.
/// </summary>
public enum AmbientOcclusionQuality
{
    /// <summary>
    /// 1 slice of 2 steps.
    /// </summary>
    Low,

    /// <summary>
    /// 2 slices of 2 steps.
    /// </summary>
    Medium,

    /// <summary>
    /// 3 slices of 3 steps.
    /// </summary>
    High,

    /// <summary>
    /// 9 slices of 3 steps.
    /// </summary>
    Ultra,
}
