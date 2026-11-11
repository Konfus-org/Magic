using System.Numerics;
using Magic.Assets;

namespace Magic.Ecs.Components;

/// <summary>
/// The world's weather as it is now: the way the air moves and the haze in it. A world has one, on an entity of its
/// own, which a script may change as it runs; whatever draws the air reads it, and so may anything the wind moves.
/// Calm, clear air is no wind and no haze. A breeze and a light haze:
/// <code>
/// { "wind": { "x": 6, "y": 0, "z": 3 }, "fogDensity": 0.01 }
/// </code>
/// A script thickens the haze as it runs: <c>weather.FogDensity = 0.03f;</c>.
/// </summary>
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
    /// How much of the light the haze scatters goes onward rather than everywhere, from -1 to 1: 0 evenly, towards 1
    /// mostly ahead, as mist does, so it glows towards the sun.
    /// </summary>
    public float FogAnisotropy = 0.3f;

    public Weather() { }
}
