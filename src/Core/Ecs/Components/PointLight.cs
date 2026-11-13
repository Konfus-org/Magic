using Magic.Assets;

namespace Magic.Ecs.Components;

/// <summary>
/// Light from a small sphere at the position of its entity, that shines in all directions.
/// </summary>
/// <remarks>
/// A new one is a white 800 lumen bulb, 5 cm across.
/// </remarks>
public record struct PointLight : IComponent
{
    public Color Color = new(1, 1, 1);

    /// <summary>
    /// Luminous intensity in candela, lumens / 4π: an 800 lumen bulb is 64.
    /// </summary>
    public float Intensity = 64;

    /// <summary>
    /// Metres that it reaches: its light fades to nothing there.
    /// </summary>
    /// <remarks>
    /// The part that draws the light can leave it out of everything farther. 0 means where its light falls below
    /// <see cref="SpotLight.FaintestLux"/>, 57 m for the default bulb, as <see cref="Reach"/> calculates.
    /// </remarks>
    public float Range;

    /// <summary>
    /// Metres from the bulb's centre to its surface, which widens its highlights as a real bulb's are.
    /// </summary>
    public float Radius = 0.05f;

    /// <summary>
    /// Whether it casts no shadow, so that its light goes through everything at almost no cost to draw.
    /// </summary>
    /// <remarks>
    /// Use it for a far light, whose shadows would not show.
    /// </remarks>
    public bool IsShadowless;

    public PointLight() { }

    /// <summary>
    /// Metres it reaches: its <see cref="Range"/>, or where its light fades out when that is 0.
    /// </summary>
    public readonly float Reach => Range == 0 ? SpotLight.GetReach(Intensity) : Range;
}
