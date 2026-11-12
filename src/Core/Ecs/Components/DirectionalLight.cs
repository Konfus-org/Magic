using Magic.Assets;

namespace Magic.Ecs.Components;

/// <summary>
/// What a <see cref="DirectionalLight"/> is to the world's sky.
/// </summary>
public enum DirectionalLightMode : byte
{
    /// <summary>
    /// A light alone: it lights the world and is nothing in its sky.
    /// </summary>
    Default,

    /// <summary>
    /// The sun: the sky draws its disc and the light that the air scatters from it.
    /// </summary>
    /// <remarks>
    /// Its light reaches the world through the air and the clouds of the sky. Its intensity is that of the sun above
    /// the air, about 120 000 lux.
    /// </remarks>
    Sun,

    /// <summary>
    /// The moon: the sky draws it full.
    /// </summary>
    /// <remarks>
    /// Its light reaches the world through the air of the sky when the sun is below the horizon. Its intensity is that
    /// of a full moon, about 0.3 lux.
    /// </remarks>
    Moon,
}

/// <summary>
/// Light from infinitely far away, like that of the sun, that shines along the +Z of its entity.
/// </summary>
/// <remarks>
/// <see cref="Intensity"/>, in lux, scales <see cref="Color"/>. One light lights the world at a time: the sun while it
/// is above the horizon, else the moon while it is above, else the first. <see cref="Mode"/> says whether it is also
/// the sun or the moon that the sky draws, in its colour and along its direction. The scale of its entity sets the
/// width of the disc, and 1 is the width as seen from the Earth.
/// </remarks>
public record struct DirectionalLight : IComponent
{
    public Color Color;

    public float Intensity;

    public DirectionalLightMode Mode;
}
