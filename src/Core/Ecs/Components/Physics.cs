using System.Numerics;

namespace Magic.Ecs.Components;

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
/// What a <see cref="Physics"/> body collides as, fitted to the model of the entity at the scale of its transform.
/// </summary>
/// <remarks>
/// A box takes the bounds of the model, a sphere takes the largest extent as its diameter, a capsule takes the height
/// and width, and a mesh takes the triangles. Only a static body can be a mesh. Without a model, the shape fits a unit
/// cube.
/// </remarks>
public enum Shape : byte
{
    Box,

    Sphere,

    Capsule,

    Mesh,
}

/// <summary>
/// A solid body: what moves it, its shape and its mass.
/// </summary>
/// <remarks>
/// <para>
/// A new one, <c>{}</c> in a file, is a body that never moves, with the shape of the model of the entity.
/// <c>{ "motion": "dynamic" }</c> is one that falls.
/// </para>
/// <para>
/// The physics gem makes a body for each entity with a <see cref="Transform"/> and this component. It moves the
/// transform as the body moves, and reads the fields back: a change to the transform moves the body there, and a
/// change to <see cref="Velocity"/> launches it. The size of the shape is that of the model times the scale of the
/// transform, so a unit cube at scale 2 is a 2 m box.
/// </para>
/// </remarks>
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
