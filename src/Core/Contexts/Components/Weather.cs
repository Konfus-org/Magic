using Magic.Contexts.Rendering;
using Magic.Interfaces;

namespace Magic.Contexts.Components;

/// <summary>
/// The world's weather as it is now: its <see cref="Wind"/>, <see cref="Clouds"/>, <see cref="Fog"/> and
/// <see cref="Air"/>, each listed or not. A world has one, on an entity of its own, which a script may change as it
/// runs; whatever draws the sky and the air reads it, and so may anything the wind moves. Calm air, a clear sky and no
/// fog are none listed. A breeze and a few clouds:
/// <code>
/// { "effects": [
///     { "type": "Wind", "velocity": { "x": 6, "y": 0, "z": 3 } },
///     { "type": "Clouds", "coverage": 0.3 } ] }
/// </code>
/// A script thickens the fog as it runs:
/// <c>if (weather.Effects.TryGet(out Fog fog)) weather.Effects.Set(fog with { Density = fog.Density * 1.5f });</c>.
/// </summary>
public record struct Weather : IComponent
{
    public RenderEffects<IWeatherEffect> Effects;
}
