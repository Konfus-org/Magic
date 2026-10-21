using System.Numerics;
using Magic.Contexts.Assets;
using Magic.Interfaces;

namespace Magic.Contexts.Components;

/// <summary>
/// The world's weather as it is now: the wind, the clouds it carries, and the fog. A world has one, on an entity of its
/// own, which a script may change as it runs; whatever draws the sky and the air reads it, and so may anything the wind
/// moves. Calm air, a clear sky and no fog are zeros, so <c>new Weather()</c>, or <c>{}</c> in a file, is that; a zero
/// density or colour reads as its default, as it means nothing of its own. Pink and purple clouds, beside a coverage:
/// <c>{ "cloudColor": { "r": 1, "g": 0.55, "b": 0.75 }, "cloudShadow": { "r": 0.35, "g": 0.2, "b": 0.55 } }</c>; mist
/// in the valleys, thinning with height as a curve says: <c>{ "fog": 0.0015, "fogFalloff": { "id": 3363 } }</c>.
/// </summary>
public record struct Weather : IComponent
{
    /// <summary>
    /// The way the air moves, in metres per second: its direction and its strength in one.
    /// </summary>
    public Vector3 Wind;

    /// <summary>
    /// How much of the sky the clouds cover, from 0, clear, to 1, overcast.
    /// </summary>
    public float Coverage;

    /// <summary>
    /// How much fog there is: the share of the light it puts out a metre where its falloff is 1; zero, none.
    /// </summary>
    public float Fog;

    /// <summary>
    /// Where the fog lies by height: its share of <see cref="Fog"/> by metres above the world's origin; none, whatever
    /// draws the fog's own, a ground fog.
    /// </summary>
    public Handle<Curve> FogFalloff;

    /// <summary>
    /// Metres from the camera before the fog begins.
    /// </summary>
    public float FogStart;

    /// <summary>
    /// How thick the clouds are, from wisps the sky shows through to banks that hide it; zero reads as halfway.
    /// </summary>
    public float Density
    {
        readonly get => field == 0 ? 0.5f : field;
        set;
    }

    /// <summary>
    /// The clouds' colour where the sun lights them, times the sun's own: white by default, so a sunset reddens them.
    /// </summary>
    public Color CloudColor
    {
        readonly get => field == default ? new Color(1, 1, 1) : field;
        set;
    }

    /// <summary>
    /// The clouds' colour where they are in their own shade and underneath, times the sky's light: a blue grey by
    /// default.
    /// </summary>
    public Color CloudShadow
    {
        readonly get => field == default ? new Color(0.45f, 0.5f, 0.6f) : field;
        set;
    }

    /// <summary>
    /// The fog's tint, times the light that reaches it: white by default, as a fog has no colour of its own and takes
    /// the sun's and the sky's.
    /// </summary>
    public Color FogColor
    {
        readonly get => field == default ? new Color(1, 1, 1) : field;
        set;
    }
}
