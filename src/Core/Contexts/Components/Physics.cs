using System.Numerics;
using Magic.Interfaces;

namespace Magic.Contexts.Components;

/// <summary>
/// A solid thing: what moves it, its shape, its surface and its mass, in one component. A zero reads as its default,
/// so <c>{}</c> in a file, or <c>new Physics()</c>, is a body that never moves, shaped like the entity's model, with
/// a normal surface; <c>{ "motion": "dynamic" }</c> is one that falls, <c>{ "surface": "slippery" }</c> one that
/// slides. The physics gem makes a body for every entity with a <see cref="Transform"/> and this, moves the
/// transform as the body moves, and reads the fields back: a change to the transform of a body moves it there, and a
/// change to <see cref="Velocity"/> launches it. The shape's size is the model's, times the transform's scale, so a
/// unit cube scaled 2 is a 2 m box with no size written anywhere twice.
/// </summary>
public record struct Physics : IComponent
{
    /// <summary>
    /// Whether the body stays put, falls and is pushed, or is moved by code and pushes others.
    /// </summary>
    public Motion Motion;

    /// <summary>
    /// What collides, fitted to the model: a box by default.
    /// </summary>
    public Shape Shape;

    /// <summary>
    /// How much it grips what it touches, from slippery to sticky.
    /// </summary>
    public Surface Surface;

    /// <summary>
    /// How much of a hit bounces back, 0 to 1: a dropped ball with 0.8 bounces to most of its height, with 0 it stays.
    /// </summary>
    public float Bounce;

    /// <summary>
    /// Kilograms; 0 is the mass of its volume in water, so a 1 m cube is a tonne and a 10 cm one a kilogram.
    /// </summary>
    public float Mass;

    /// <summary>
    /// Metres per second, as the body moves after each step; set it to throw the body.
    /// </summary>
    public Vector3 Velocity;

    /// <summary>
    /// Radians per second about each axis, as the body turns after each step; set it to spin the body.
    /// </summary>
    public Vector3 AngularVelocity;
}

/// <summary>
/// How a <see cref="Physics"/> body moves.
/// </summary>
public enum Motion : byte
{
    /// <summary>
    /// Never, of itself: ground and walls. Moving its transform moves it, at no cost while it stays.
    /// </summary>
    Static,

    /// <summary>
    /// Falls, bounces and is pushed; its transform is the body's to write.
    /// </summary>
    Dynamic,

    /// <summary>
    /// Moved by code, through its transform, and pushes what it meets as something infinitely heavy: platforms and
    /// doors.
    /// </summary>
    Kinematic,
}

/// <summary>
/// Which kinds of body a query considers: any set of <see cref="Motion"/>.
/// </summary>
[Flags]
public enum Motions : byte
{
    None = 0,

    Static = 1,

    Dynamic = 2,

    Kinematic = 4,

    All = Static | Dynamic | Kinematic,
}

/// <summary>
/// What a <see cref="Physics"/> body collides as, fitted to the entity's model at the transform's scale: its bounds
/// for a box, the largest extent for a sphere's diameter, the height and width for a capsule, and its triangles for a
/// mesh, which only a static body can be. Without a model, a unit cube's.
/// </summary>
public enum Shape : byte
{
    Box,

    Sphere,

    Capsule,

    Mesh,
}

/// <summary>
/// How a <see cref="Physics"/> body grips what it touches: a range from ice to glue, a step at a time.
/// </summary>
public enum Surface : byte
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
