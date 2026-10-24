using Magic.Contexts.Assets;
using Magic.Interfaces;

namespace Magic.Contexts.Components;

/// <summary>
/// Light from a small sphere at its entity's position, shining in a cone along its +Z. A zero reads as its default, so
/// <c>new SpotLight()</c>, or <c>{}</c> in a file, is a white 800 lumen spot, 60 degrees across.
/// </summary>
public record struct SpotLight : IComponent
{
    /// <summary>
    /// The illuminance, in lux, below which a point or spot light's light counts as gone, where its default range ends:
    /// about a full moon's.
    /// </summary>
    public const float FaintestLux = 0.02f;

    public Color Color
    {
        readonly get => field == default ? new Color(1, 1, 1) : field;
        set;
    }

    /// <summary>
    /// Luminous intensity in candela, lumens / π, so narrowing the cone does not brighten it: an 800 lumen spot is 255.
    /// </summary>
    public float Intensity
    {
        readonly get => field == 0 ? 255 : field;
        set;
    }

    /// <summary>
    /// Metres it reaches: its light fades to nothing there, so whatever draws it can leave it out of everything
    /// farther. By default where its light falls below <see cref="FaintestLux"/>, 113 m for the default spot.
    /// </summary>
    public float Range
    {
        readonly get => field == 0 ? GetReach(Intensity) : field;
        set;
    }

    /// <summary>
    /// Metres from the bulb's centre to its surface, which widens its highlights as a real bulb's are.
    /// </summary>
    public float Radius
    {
        readonly get => field == 0 ? 0.05f : field;
        set;
    }

    /// <summary>
    /// Degrees from the cone's axis to its edge, where the light ends.
    /// </summary>
    public float OuterAngle
    {
        readonly get => field == 0 ? 30 : field;
        set;
    }

    /// <summary>
    /// Degrees from the cone's axis to where the light starts fading towards the edge; by default three quarters of
    /// <see cref="OuterAngle"/>.
    /// </summary>
    public float InnerAngle
    {
        readonly get => field == 0 ? OuterAngle * 0.75f : field;
        set;
    }

    /// <summary>
    /// It casts no shadow: its light reaches through everything, which costs next to nothing to draw. A light far off,
    /// whose shadows would not show, is best drawn so.
    /// </summary>
    public bool IsShadowless { get; set; }

    /// <summary>
    /// Metres from a light of <paramref name="intensity"/> candela at which its light falls to
    /// <see cref="FaintestLux"/>.
    /// </summary>
    public static float GetReach(float intensity) => MathF.Sqrt(Math.Max(intensity, 0) / FaintestLux);
}
