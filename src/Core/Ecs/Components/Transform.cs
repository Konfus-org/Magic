using System.Numerics;

namespace Magic.Ecs.Components;

/// <summary>
/// Where an entity is, which way it faces and how big it is, in metres.
/// </summary>
/// <remarks>
/// +Y is up, +X right and +Z forward, left-handed: a camera looks, and a directional light shines, along the +Z of its
/// entity. A new one is at the origin, not rotated, at unit scale.
/// </remarks>
public record struct Transform : IComponent
{
    public Vector3 Position;

    public Quaternion Rotation = Quaternion.Identity;

    public Vector3 Scale = Vector3.One;

    public Transform() { }

    /// <summary>
    /// Scale, then rotation, then position: what takes the entity's model into the world.
    /// </summary>
    public readonly Matrix4x4 Matrix =>
        Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Position);

    /// <summary>
    /// Returns this transform of a child, relative to <paramref name="parent"/>, as the world sees it.
    /// </summary>
    /// <remarks>
    /// The parent scales it, turns it, then moves it.
    /// </remarks>
    public readonly Transform Under(Transform parent) =>
        new()
        {
            Position = parent.Position + Vector3.Transform(Position * parent.Scale, parent.Rotation),
            Rotation = parent.Rotation * Rotation,
            Scale = parent.Scale * Scale,
        };
}
