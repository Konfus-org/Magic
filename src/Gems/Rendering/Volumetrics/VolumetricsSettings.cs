using System.ComponentModel;

namespace Magic.Volumetrics;

/// <summary>
/// How finely the air is filled, which sets what it costs; the world's weather says how it looks.
/// </summary>
[Category("Rendering")]
public sealed record VolumetricsSettings
{
    /// <summary>
    /// The volume's cells across and down, as a part of the picture's size along each side, from 0 to 1. Smaller is
    /// faster and softer.
    /// </summary>
    public float VolumetricsScale { get; init; } = 0.083f;

    /// <summary>
    /// The metres the volume reaches from the camera; past it, the haze is drawn without shadows.
    /// </summary>
    public float VolumetricsDistance { get; init; } = 128;
}
