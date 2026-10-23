using Magic.Contexts.Assets;
using Magic.Interfaces;

namespace Magic.Contexts.Rendering;

/// <summary>
/// The fog: how much there is, the share of the light it puts out a metre where its falloff is 1; its tint, times the
/// light that reaches it, white by default, as a fog has no colour of its own and takes the sun's and the sky's; the
/// metres from the camera before it begins; and where it lies by height, its share of the density by metres above the
/// world's origin, none for whatever draws the fog's own, a ground fog. Mist in the valleys, thinning with height:
/// <c>{ "type": "Fog", "density": 0.0015, "falloff": { "id": 3680 } }</c>.
/// </summary>
public readonly record struct Fog(
    float Density = 0,
    Color Color = default,
    float Start = 0,
    Handle<Curve> Falloff = default) : IWeatherEffect
{
    public Color Color { get => field == default ? new Color(1, 1, 1) : field; init; } = Color;
}
