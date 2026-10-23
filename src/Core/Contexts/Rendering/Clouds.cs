using Magic.Contexts.Assets;
using Magic.Interfaces;

namespace Magic.Contexts.Rendering;

/// <summary>
/// The clouds: how much of the sky they cover, from 0, clear, to 1, overcast; how thick they are; and their colours. A
/// zero density or colour reads as its default. Pink clouds over half the sky:
/// <c>{ "type": "Clouds", "coverage": 0.5, "color": { "r": 1, "g": 0.55, "b": 0.75 } }</c>.
/// </summary>
public readonly record struct Clouds(
    float Coverage = 0,
    float Density = 0,
    Color Color = default,
    Color Shadow = default) : IWeatherEffect
{
    /// <summary>
    /// How thick the clouds are, from wisps the sky shows through to banks that hide it; zero reads as halfway.
    /// </summary>
    public float Density { get => field == 0 ? 0.5f : field; init; } = Density;

    /// <summary>
    /// The clouds' colour where the sun lights them, times the sun's own: white by default, so a sunset reddens them.
    /// </summary>
    public Color Color { get => field == default ? new Color(1, 1, 1) : field; init; } = Color;

    /// <summary>
    /// The clouds' colour where they are in their own shade and underneath, times the sky's light: a blue grey by
    /// default.
    /// </summary>
    public Color Shadow { get => field == default ? new Color(0.45f, 0.5f, 0.6f) : field; init; } = Shadow;
}
