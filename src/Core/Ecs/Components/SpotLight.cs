using Magic.Files.Types;

namespace Magic.Ecs.Components;

/// <summary>
/// Light from a small sphere at its entity's position, shining in a cone along its +Z. A new one is a white 800 lumen
/// spot, 60 degrees across.
/// </summary>
public record struct SpotLight : IComponent
{
    /// <summary>
    /// The illuminance, in lux, below which a point or spot light's light counts as gone, where its default range ends:
    /// about a full moon's.
    /// </summary>
    public const float FaintestLux = 0.02f;

    public Color Color = new(1, 1, 1);

    /// <summary>
    /// Luminous intensity in candela, lumens / π, so narrowing the cone does not brighten it: an 800 lumen spot is 255.
    /// </summary>
    public float Intensity = 255;

    /// <summary>
    /// Metres it reaches: its light fades to nothing there, so whatever draws it can leave it out of everything
    /// farther. 0 is where its light falls below <see cref="FaintestLux"/>, 113 m for the default spot, as
    /// <see cref="Reach"/> works out.
    /// </summary>
    public float Range;

    /// <summary>
    /// Metres from the bulb's centre to its surface, which widens its highlights as a real bulb's are.
    /// </summary>
    public float Radius = 0.05f;

    /// <summary>
    /// Degrees from the cone's axis to its edge, where the light ends.
    /// </summary>
    public float OuterAngle = 30;

    /// <summary>
    /// Degrees from the cone's axis to where the light starts fading towards the edge. 0 is three quarters of
    /// <see cref="OuterAngle"/>, as <see cref="FadeAngle"/> works out.
    /// </summary>
    public float InnerAngle;

    /// <summary>
    /// It casts no shadow: its light reaches through everything, which costs next to nothing to draw. A light far off,
    /// whose shadows would not show, is best drawn so.
    /// </summary>
    public bool IsShadowless;

    public SpotLight() { }

    /// <summary>
    /// Metres it reaches: its <see cref="Range"/>, or where its light fades out when that is 0.
    /// </summary>
    public readonly float Reach => Range == 0 ? GetReach(Intensity) : Range;

    /// <summary>
    /// Degrees from the cone's axis to where its light starts fading: its <see cref="InnerAngle"/>, or three quarters
    /// of <see cref="OuterAngle"/> when that is 0.
    /// </summary>
    public readonly float FadeAngle => InnerAngle == 0 ? OuterAngle * 0.75f : InnerAngle;

    /// <summary>
    /// Metres from a point or spot light of <paramref name="intensity"/> candela at which its light falls to
    /// <see cref="FaintestLux"/>.
    /// </summary>
    public static float GetReach(float intensity) => MathF.Sqrt(Math.Max(intensity, 0) / FaintestLux);
}
