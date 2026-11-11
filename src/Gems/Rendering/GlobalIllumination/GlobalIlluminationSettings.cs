using System.ComponentModel;

namespace Magic.GlobalIllumination;

/// <summary>
/// How the light that bounces is traced.
/// </summary>
[Category("Rendering")]
public sealed record GlobalIlluminationSettings
{
    /// <summary>
    /// Whether the light that bounces is traced; off, the sky lights the surfaces.
    /// </summary>
    public bool IsIndirectLightOn { get; init; } = true;

    /// <summary>
    /// Whether smooth surfaces' reflections are traced; off, they reflect their diffuse light.
    /// </summary>
    public bool IsReflectionsOn { get; init; } = true;

    /// <summary>
    /// The size the diffuse light and the reflections are traced at, as a part of the picture's size along each side,
    /// from 0 to 1. Smaller is faster and softer.
    /// </summary>
    public float IndirectScale { get; init; } = 0.25f;

    /// <summary>
    /// How many probes the grid holds along each side of the ground, from 2 to 48. More hold the light in finer detail
    /// and take longer to update; a change starts the probes afresh.
    /// </summary>
    public int ProbesAcross { get; init; } = 32;

    /// <summary>
    /// How many probes the grid holds upwards, from 2 to 16; a change starts the probes afresh.
    /// </summary>
    public int ProbesUp { get; init; } = 8;
}
