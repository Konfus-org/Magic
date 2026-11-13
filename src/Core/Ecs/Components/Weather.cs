using System.Numerics;
using Magic.Assets;

namespace Magic.Ecs.Components;

/// <summary>
/// The weather of the world now: the way the air moves, the <see cref="Fog"/> in it and the <see cref="Clouds"/> over
/// it.
/// </summary>
/// <remarks>
/// <para>
/// A world has one, on an entity of its own, which a script can change while it runs. The part that draws the air
/// reads it, and so can anything that the wind moves.
/// </para>
/// <para>
/// Calm, clear air has no wind, no haze and no clouds. A breeze, a light haze and scattered clouds:
/// <code>
/// { "wind": { "x": 6, "y": 0, "z": 3 }, "fog": { "density": 0.01 }, "clouds": { "cover": 0.4 } }
/// </code>
/// A script makes the haze thicker while it runs: <c>weather.Fog.Density = 0.03f;</c>.
/// </para>
/// </remarks>
public record struct Weather : IComponent
{
    /// <summary>
    /// The way the air moves, its direction and its strength in one, in metres per second.
    /// </summary>
    public Vector3 Wind;

    /// <summary>
    /// The haze in the air.
    /// </summary>
    public Fog Fog = new();

    /// <summary>
    /// The clouds over the sky.
    /// </summary>
    public Clouds Clouds = new();

    public Weather() { }
}

/// <summary>
/// The haze of a <see cref="Weather"/>: how thick it is at each height, how it scatters light, and how finely and how
/// far it is drawn with shadows.
/// </summary>
public record struct Fog
{
    /// <summary>
    /// How much of the light a metre of the haze scatters at <see cref="Height"/>, from 0 to 1: 0 is clear air.
    /// </summary>
    public float Density;

    /// <summary>
    /// The height in metres where the haze is as thick as <see cref="Density"/> says.
    /// </summary>
    public float Height;

    /// <summary>
    /// The metres above <see cref="Height"/> at which the haze is half as thick.
    /// </summary>
    public float HalfHeight = 20;

    /// <summary>
    /// What the haze scatters light into: white for clean haze.
    /// </summary>
    public Color Color = new(1, 1, 1);

    /// <summary>
    /// How much of the light that the haze scatters goes forward, from -1 to 1: 0 is even, 1 is mostly forward.
    /// </summary>
    /// <remarks>
    /// Mist scatters mostly forward, so it glows towards the sun.
    /// </remarks>
    public float Anisotropy = 0.3f;

    /// <summary>
    /// The cells across and down of the volume that draws the haze near the camera with shadows and local lights, as
    /// a part of the picture's size along each side, from 0 to 1. Smaller is faster and softer.
    /// </summary>
    public float VolumeScale = 0.083f;

    /// <summary>
    /// The metres that volume reaches from the camera; past it, the haze is drawn without shadows.
    /// </summary>
    public float VolumeDistance = 128;

    public Fog() { }
}

/// <summary>
/// The clouds of a <see cref="Weather"/>: how much of the sky they cover, and how high they are.
/// </summary>
public record struct Clouds
{
    /// <summary>
    /// How much of the sky the clouds cover, from 0, clear, to 1, overcast. The wind carries them.
    /// </summary>
    public float Cover;

    /// <summary>
    /// The height in metres of the clouds' base; they reach 2 km above it.
    /// </summary>
    public float Height = 1500;

    public Clouds() { }
}
