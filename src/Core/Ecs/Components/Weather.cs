using System.Numerics;

namespace Magic.Ecs.Components;

/// <summary>
/// The world's weather as it is now: the way the air moves, and the effects the air is drawn with, each a material
/// such as the fog, in order. A world has one, on an entity of its own, which a script may change as it runs; whatever
/// draws the air reads it, and so may anything the wind moves. Calm, clear air is no wind and no effects. A breeze and
/// a light haze:
/// <code>
/// { "wind": { "x": 6, "y": 0, "z": 3 },
///   "effects": [ { "handle": { "id": 8828027672504642575 }, "overrides": { "density": { "x": 0.01 } } } ] }
/// </code>
/// A script thickens the fog as it runs: <c>weather.Effects[0].Overrides["density"] = 0.03f;</c>.
/// </summary>
public record struct Weather : IComponent
{
    /// <summary>
    /// The way the air moves, its direction and its strength in one, in metres per second.
    /// </summary>
    public Vector3 Wind;

    public MaterialSlots Effects;
}
