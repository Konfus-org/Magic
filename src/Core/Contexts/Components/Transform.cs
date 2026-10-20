using System.Numerics;
using Magic.Interfaces;

namespace Magic.Contexts.Components;

/// <summary>
/// Where an entity is, which way it faces and how big it is, in metres. +Y is up, +X right and +Z forward, left-handed:
/// a camera looks, and a directional light shines, along its entity's +Z.
/// <para>
/// A new Transform is at the origin, unrotated, at unit scale. Its defaults are initialisers rather than getters, as
/// other components' are, because a zero scale is a real one; so <c>default(Transform)</c>, with no rotation and no
/// size, is not a transform.
/// </para>
/// </summary>
public record struct Transform : IComponent
{
    public Vector3 Position;

    public Quaternion Rotation = Quaternion.Identity;

    public Vector3 Scale = Vector3.One;

    public Transform()
    {
    }

    /// <summary>
    /// Scale, then rotation, then position: what takes the entity's model into the world.
    /// </summary>
    public readonly Matrix4x4 Matrix => Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Position);

    /// <summary>
    /// This transform, a child's, relative to <paramref name="parent"/>, as the world sees it: scaled, turned, then
    /// moved by the parent's. A prefab's part is placed under its parent's place this way.
    /// </summary>
    public readonly Transform Under(Transform parent)
    {
        return new()
        {
            Position = parent.Position + Vector3.Transform(Position * parent.Scale, parent.Rotation),
            Rotation = parent.Rotation * Rotation,
            Scale = parent.Scale * Scale,
        };
    }
}
