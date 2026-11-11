using System.ComponentModel;

namespace Magic.TracedShadows;

/// <summary>
/// How the shadows are traced.
/// </summary>
[Category("Rendering")]
public sealed record TracedShadowsSettings
{
    /// <summary>
    /// The size the shadows are traced and denoised at, as a part of the picture's size along each side, from 0 to 1;
    /// they are brought back to the picture's size along its surfaces' edges. Smaller is faster and softer.
    /// </summary>
    public float ShadowScale { get; init; } = 0.75f;
}
