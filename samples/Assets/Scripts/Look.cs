using System.Numerics;

namespace Magic.Samples.Assets.Scripts;

/// <summary>
/// One look <see cref="Looks"/> eases the world to: the params it gives the tonemap, the bloom and the samples' grade,
/// each at the value that changes nothing unless it says, as a prefab writes it:
/// <c>{ "name": "Noir", "saturation": 0, "contrast": 1.35, "vignette": 0.55 }</c>.
/// </summary>
internal sealed class Look
{
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// EV added before the tonemap.
    /// </summary>
    public float Exposure { get; set; }

    /// <summary>
    /// How strongly what is bright glows.
    /// </summary>
    public float Bloom { get; set; } = 0.04f;

    public float Saturation { get; set; } = 1;

    public float Contrast { get; set; } = 1;

    public Vector3 Tint { get; set; } = Vector3.One;

    public float Vignette { get; set; }
}
