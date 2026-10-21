using Magic.Contexts.Assets;
using Magic.Interfaces;

namespace Magic.Contexts.Components;

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
    /// The moon: the sky draws it, lit from the sun's side, so its phase follows from where the sun is, and its light
    /// reaches the world through the sky's air and clouds. Its intensity is a full moon's, about 0.3 lux.
    /// </summary>
    Moon,
}

/// <summary>
/// Light from infinitely far away, like the sun's, shining along its entity's +Z. <see cref="Intensity"/>, in lux,
/// scales <see cref="Color"/>. Every one lights the world; its <see cref="Mode"/> says whether it is also the sun or
/// the moon the world's sky draws, in its colour and along its direction, with a disc as wide as its entity's scale
/// says, 1 as they look from the Earth: <c>{ "mode": "sun", "intensity": 120000 }</c>. A sun or a moon below the
/// horizon lights nothing, as the ground is in the way.
/// </summary>
public record struct DirectionalLight : IComponent
{
    public Color Color;

    public float Intensity;

    /// <summary>
    /// Whether it is a light alone, the world's sun or the world's moon.
    /// </summary>
    public DirectionalLightMode Mode;
}
