using Magic.Assets.Types;

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
    /// The sun: the sky draws its disc and the light the air scatters of it, and its light reaches the world through
    /// the sky's air and clouds. Its intensity is the sun's above the air, about 120 000 lux.
    /// </summary>
    Sun,

    /// <summary>
    /// The moon: the sky draws it full, and its light reaches the world through the sky's air once the sun is below
    /// the horizon. Its intensity is a full moon's, about 0.3 lux.
    /// </summary>
    Moon,
}

/// <summary>
/// Light from infinitely far away, like the sun's, shining along its entity's +Z. <see cref="Intensity"/>, in lux,
/// scales <see cref="Color"/>. One lights the world at a time: the sun while it is above the horizon, else the moon
/// while it is, else the first there is; its <see cref="Mode"/> says whether it is also the sun or the moon the
/// world's sky draws, in its colour and along its direction, with a disc as wide as its entity's scale says, 1 as
/// they look from the Earth.
/// </summary>
public record struct DirectionalLight : IComponent
{
    public Color Color;

    public float Intensity;

    public DirectionalLightMode Mode;
}
