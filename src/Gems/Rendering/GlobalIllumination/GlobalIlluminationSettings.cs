using System.ComponentModel;

namespace Magic.GlobalIllumination;

/// <summary>
/// How the light that bounces is traced.
/// </summary>
[Category("Rendering")]
public sealed record GlobalIlluminationSettings
{
    /// <summary>
    /// The size the diffuse light and the reflections are traced at, as a part of the picture's size along each side,
    /// from 0 to 1. Smaller is faster and softer.
    /// </summary>
    public float IndirectScale { get; init; } = 0.25f;
}
