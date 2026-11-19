using System.ComponentModel;
using Magic.Configuration;

namespace Magic.GlobalIllumination;

/// <summary>
/// How the light that bounces is traced.
/// </summary>
[Category("Rendering")]
public sealed class GlobalIlluminationSettings : ISettings
{
    /// <summary>
    /// Whether the light that bounces is traced; off, the sky lights the surfaces.
    /// </summary>
    public bool IsIndirectLightOn { get; set; } = true;

    /// <summary>
    /// The size the diffuse light and the reflections are traced at, as a part of the picture's size along each side,
    /// from 0 to 1. Smaller is faster and softer.
    /// </summary>
    public float IndirectScale { get; set; } = 0.5f;

    /// <summary>
    /// How many probes the grid around the camera holds along each side of the ground, from 2 to 48. More reach
    /// further and take longer to update; a change starts the probes afresh.
    /// </summary>
    public int ProbesAcross { get; set; } = 32;

    /// <summary>
    /// How many probes the grid holds upwards, from 2 to 16; a change starts the probes afresh.
    /// </summary>
    public int ProbesUp { get; set; } = 8;

    /// <summary>
    /// The metres between neighbouring probes, from 0.1. Less holds the light in finer detail and reaches less far; a
    /// change starts the probes afresh.
    /// </summary>
    public float ProbeSpacing { get; set; } = 2;

    /// <summary>
    /// How many rays each traced pixel of the diffuse light sends a frame, from 1 to 8. More is less grainy and slower.
    /// </summary>
    public int IndirectRayCount { get; set; } = 4;

    /// <summary>
    /// How far in metres the diffuse light's rays reach, from 0, before the probes' light stands for what lies beyond.
    /// </summary>
    public float IndirectRayRange { get; set; } = 10;

    /// <summary>
    /// The most of its last frames the share of the sky that each traced pixel sees keeps each frame, from 0 to 1. More
    /// is smoother and slower to follow a change. NRD keeps the diffuse light's own history.
    /// </summary>
    public float IndirectHistory { get; set; } = 0.98f;

    /// <summary>
    /// How much of its rays' light a probe takes each time it is given rays, from 0 to 1. More follows a change sooner
    /// and flickers more.
    /// </summary>
    public float ProbeUpdateSpeed { get; set; } = 0.5f;
}
