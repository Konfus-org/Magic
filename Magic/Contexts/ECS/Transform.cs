using Magic.Extensions;
using System.Numerics;

namespace Magic.Contexts.Components;

/// <summary>
/// Where an entity is, which way it faces and how big it is, relative to its parent.
/// </summary>
public struct Transform : IComponent
{
    public static readonly Transform Identity = new()
    {
        Position = Vector3.Zero,
        Rotation = Quaternion.Identity,
        Scale = Vector3.One,
    };

    /// <summary>
    /// At the origin, unrotated, unit scale, so an initialiser only has to name what differs. JSON goes through
    /// here too, so a missing rotation or scale is identity, not zero.
    /// </summary>
    public Transform()
    {
        Rotation = Quaternion.Identity;
        Scale = Vector3.One;
    }

    public Vector3 Position { get; set; }

    public Quaternion Rotation { get; set; }

    public Vector3 Scale { get; set; }

    public readonly Matrix4x4 Matrix => Matrix4x4.Trs(Position, Rotation, Scale);
}
