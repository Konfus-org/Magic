using Magic.Assets;

namespace Magic.Ecs.Components;

/// <summary>
/// Light from a small sphere at the position of its entity, that shines in a cone along its +Z.
/// </summary>
/// <remarks>
/// A new one is a white 800 lumen spot, 60 degrees across.
/// </remarks>
public record struct SpotLight : IComponent
{
    /// <summary>
    /// The illuminance in lux, about that of a full moon, below which the light of a point or spot light is gone.
    /// </summary>
    /// <remarks>
    /// The default range of such a light ends there.
    /// </remarks>
    public const float FaintestLux = 0.02f;

    public Color Color = new(1, 1, 1);

    /// <summary>
    /// Luminous intensity in candela, lumens / π, so narrowing the cone does not brighten it: an 800 lumen spot is 255.
    /// </summary>
    public float Intensity = 255;

    /// <summary>
    /// Metres that it reaches: its light fades to nothing there.
    /// </summary>
    /// <remarks>
    /// The part that draws the light can leave it out of everything farther. 0 means where its light falls below
    /// <see cref="FaintestLux"/>, 113 m for the default spot, as <see cref="Reach"/> calculates.
    /// </remarks>
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
    /// Degrees from the axis of the cone to where the light starts to fade towards the edge.
    /// </summary>
    /// <remarks>
    /// 0 means three quarters of <see cref="OuterAngle"/>, as <see cref="FadeAngle"/> calculates.
    /// </remarks>
    public float InnerAngle;

    /// <summary>
    /// Whether it casts no shadow, so that its light goes through everything at almost no cost to draw.
    /// </summary>
    /// <remarks>
    /// Use it for a far light, whose shadows would not show.
    /// </remarks>
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
