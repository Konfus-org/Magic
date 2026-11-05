using System.Numerics;

namespace Magic.Ecs.Components.Effects;

/// <summary>
/// The way the air moves, for whatever the wind moves to read.
/// </summary>
public record struct Wind : IWeatherEffect
{
    /// <summary>
    /// Its direction and its strength in one, in metres per second.
    /// </summary>
    public Vector3 Velocity;
}
