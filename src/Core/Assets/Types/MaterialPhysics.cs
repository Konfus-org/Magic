namespace Magic.Assets.Types;

/// <summary>
/// How a body made of a <see cref="MaterialPhysics"/> grips what it touches: a range from ice to glue, a step at a
/// time.
/// </summary>
public enum Grip : byte
{
    /// <summary>
    /// Wood, stone, most things.
    /// </summary>
    Normal,

    /// <summary>
    /// Ice: slides almost without end.
    /// </summary>
    Slippery,

    /// <summary>
    /// Polished floor or wet metal: slides, but stops.
    /// </summary>
    Slick,

    /// <summary>
    /// Rubber or rough rock: hardly slides.
    /// </summary>
    Grippy,

    /// <summary>
    /// Glue: stays where it lands, and does not roll.
    /// </summary>
    Sticky,
}

/// <summary>
/// How a body made of a <see cref="Material"/> touches others: <c>{ "grip": "slippery", "bounce": 0.8 }</c>. A new one
/// grips normally and does not bounce.
/// </summary>
public sealed class MaterialPhysics
{
    /// <summary>
    /// How much it grips what it touches, from slippery to sticky.
    /// </summary>
    public Grip Grip { get; set; }

    /// <summary>
    /// How much of a hit bounces back, 0 to 1: a dropped ball with 0.8 bounces to most of its height, with 0 it stays.
    /// </summary>
    public float Bounce { get; set; }
}
