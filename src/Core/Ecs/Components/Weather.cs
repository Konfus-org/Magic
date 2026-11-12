using System.Numerics;
using Magic.Assets;

namespace Magic.Ecs.Components;

/// <summary>
/// The weather of the world now: the way the air moves, and the haze and the clouds in it.
/// </summary>
/// <remarks>
/// <para>
/// A world has one, on an entity of its own, which a script can change while it runs. The part that draws the air
/// reads it, and so can anything that the wind moves.
/// </para>
/// <para>
/// Calm, clear air has no wind, no haze and no clouds. A breeze, a light haze and scattered clouds:
/// <code>
/// { "wind": { "x": 6, "y": 0, "z": 3 }, "fogDensity": 0.01, "cloudCover": 0.4 }
/// </code>
/// A script makes the haze thicker while it runs: <c>weather.FogDensity = 0.03f;</c>.
/// </para>
/// </remarks>
public record struct Weather : IComponent
{
    /// <summary>
    /// The way the air moves, its direction and its strength in one, in metres per second.
    /// </summary>
    public Vector3 Wind;

    /// <summary>
    /// How much of the light a metre of the haze scatters at <see cref="FogHeight"/>, from 0 to 1: 0 is clear air.
    /// </summary>
    public float FogDensity;

    /// <summary>
    /// The height in metres where the haze is as thick as <see cref="FogDensity"/> says, and the metres above it at
    /// which it is half as thick.
    /// </summary>
    public float FogHeight;

    public float FogHalfHeight = 20;

    /// <summary>
    /// What the haze scatters light into: white for clean haze.
    /// </summary>
    public Color FogColor = new(1, 1, 1);

    /// <summary>
    /// How much of the light that the haze scatters goes forward, from -1 to 1: 0 is even, 1 is mostly forward.
    /// </summary>
    /// <remarks>
    /// Mist scatters mostly forward, so it glows towards the sun.
    /// </remarks>
    public float FogAnisotropy = 0.3f;

    /// <summary>
    /// How much of the sky the clouds cover, from 0, clear, to 1, overcast. The wind carries them.
    /// </summary>
    public float CloudCover;

    /// <summary>
    /// The height in metres of the clouds' base; they reach 2 km above it.
    /// </summary>
    public float CloudHeight = 1500;

    public Weather() { }
}
