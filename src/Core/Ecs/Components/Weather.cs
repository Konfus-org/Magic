using Magic.Ecs.Components.Effects;

namespace Magic.Ecs.Components;

/// <summary>
/// The world's weather as it is now: effects such as <see cref="Wind"/> and <see cref="Fog"/>, each listed or not. A world
/// has one, on an entity of its own, which a script may change as it runs; whatever draws the air reads it, and so may
/// anything the wind moves. Calm, clear air is none listed. A breeze and a light haze:
/// <code>
/// { "effects": [
///     { "type": "Wind", "velocity": { "x": 6, "y": 0, "z": 3 } },
///     { "type": "Fog", "density": 0.01 } ] }
/// </code>
/// A script thickens the fog as it runs:
/// <c>if (weather.Effects.TryGet(out Fog fog)) weather.Effects.Set(fog with { Density = fog.Density * 1.5f });</c>.
/// </summary>
public record struct Weather : IComponent
{
    public RenderEffects<IWeatherEffect> Effects;
}
