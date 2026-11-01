using Magic.Assets.Types;

namespace Magic.Ecs.Components;

/// <summary>
/// Light from a small sphere at its entity's position, shining every way. A new one is a white 800 lumen bulb, 5 cm
/// across.
/// </summary>
public record struct PointLight : IComponent
{
    public Color Color = new(1, 1, 1);

    /// <summary>
    /// Luminous intensity in candela, lumens / 4π: an 800 lumen bulb is 64.
    /// </summary>
    public float Intensity = 64;

    /// <summary>
    /// Metres it reaches: its light fades to nothing there, so whatever draws it can leave it out of everything
    /// farther. 0 is where its light falls below <see cref="SpotLight.FaintestLux"/>, 57 m for the default bulb, as
    /// <see cref="Reach"/> works out.
    /// </summary>
    public float Range;

    /// <summary>
    /// Metres from the bulb's centre to its surface, which widens its highlights as a real bulb's are.
    /// </summary>
    public float Radius = 0.05f;

    /// <summary>
    /// It casts no shadow: its light reaches through everything, which costs next to nothing to draw. A light far off,
    /// whose shadows would not show, is best drawn so.
    /// </summary>
    public bool IsShadowless;

    public PointLight() { }

    /// <summary>
    /// Metres it reaches: its <see cref="Range"/>, or where its light fades out when that is 0.
    /// </summary>
    public readonly float Reach => Range == 0 ? SpotLight.GetReach(Intensity) : Range;
}
