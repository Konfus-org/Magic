using System.Numerics;
using Magic.Contexts.Utils;
using Magic.Interfaces;

namespace Magic.Contexts.Components;

/// <summary>
/// Where an entity is, which way it faces and how big it is, in metres. +Y is up, +X right and +Z forward, left-handed:
/// a camera looks, and a directional light shines, along its entity's +Z. Its position is in doubles, so a world can
/// be any size; what draws or measures near it works relative to a nearby origin (<see cref="RelativeTo"/>).
/// <para>
/// A new Transform is at the origin, unrotated, at unit scale. Its defaults are initialisers rather than getters, as
/// other components' are, because a zero scale is a real one; so <c>default(Transform)</c>, with no rotation and no
/// size, is not a transform.
/// </para>
/// </summary>
public record struct Transform : IComponent
{
    public Vector3D Position;

    public Quaternion Rotation = Quaternion.Identity;

    public Vector3 Scale = Vector3.One;

    public Transform()
    {
    }

    /// <summary>
    /// Scale, then rotation, then position less <paramref name="origin"/>: what takes the entity's model into a space
    /// whose (0, 0, 0) is at <paramref name="origin"/>, in floats, which are exact near it. A prefab's parts pass
    /// <c>default</c>, as theirs is the prefab's own space.
    /// </summary>
    public readonly Matrix4x4 RelativeTo(Vector3D origin)
    {
        return Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation((Position - origin).ToVector3());
    }

    /// <summary>
    /// This transform, a child's, relative to <paramref name="parent"/>, as the world sees it: scaled, turned, then
    /// moved by the parent's. A prefab's part is placed under its parent's place this way.
    /// </summary>
    public readonly Transform Under(Transform parent)
    {
        return new()
        {
            Position = parent.Position + Vector3D.Transform(Position * parent.Scale, parent.Rotation),
            Rotation = parent.Rotation * Rotation,
            Scale = parent.Scale * Scale,
        };
    }
}
