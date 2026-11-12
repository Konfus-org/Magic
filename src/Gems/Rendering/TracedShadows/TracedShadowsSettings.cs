using System.ComponentModel;
using Magic.Configuration;

namespace Magic.TracedShadows;

/// <summary>
/// How the shadows are traced.
/// </summary>
[Category("Rendering")]
public sealed class TracedShadowsSettings : ISettings
{
    /// <summary>
    /// Whether shadows are traced; off, every light reaches every surface.
    /// </summary>
    public bool IsTracedShadowsOn { get; set; } = true;

    /// <summary>
    /// The size the shadows are traced and denoised at, as a part of the picture's size along each side, from 0 to 1;
    /// they are brought back to the picture's size along its surfaces' edges. Smaller is faster and softer.
    /// </summary>
    public float ShadowScale { get; set; } = 0.75f;

    /// <summary>
    /// How many times the sun's shadow is blurred before the last blur that blends it, 1 or 2. Fewer is faster and
    /// grainier.
    /// </summary>
    public int ShadowFilterPasses { get; set; } = 2;
}
