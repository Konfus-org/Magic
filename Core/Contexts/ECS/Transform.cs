using System.Numerics;

namespace Magic.Contexts.Components;

/// <summary>Where an entity is, which way it faces and how big it is, relative to its parent.</summary>
public struct Transform
{
    public Vector3 Position { get; set; }
    public Quaternion Rotation { get; set; }
    public Vector3 Scale { get; set; }

    // Perf-driven: same result as CreateScale * CreateFromQuaternion * CreateTranslation, built without the
    // two 4x4 multiplies. Every entity's world matrix comes through here each frame.
    public readonly Matrix4x4 Matrix
    {
        get
        {
            float xx = Rotation.X * Rotation.X, yy = Rotation.Y * Rotation.Y, zz = Rotation.Z * Rotation.Z;
            float xy = Rotation.X * Rotation.Y, xz = Rotation.X * Rotation.Z, yz = Rotation.Y * Rotation.Z;
            float wx = Rotation.W * Rotation.X, wy = Rotation.W * Rotation.Y, wz = Rotation.W * Rotation.Z;

            return new Matrix4x4(
                Scale.X * (1f - (2f * (yy + zz))), Scale.X * 2f * (xy + wz), Scale.X * 2f * (xz - wy), 0f,
                Scale.Y * 2f * (xy - wz), Scale.Y * (1f - (2f * (xx + zz))), Scale.Y * 2f * (yz + wx), 0f,
                Scale.Z * 2f * (xz + wy), Scale.Z * 2f * (yz - wx), Scale.Z * (1f - (2f * (xx + yy))), 0f,
                Position.X, Position.Y, Position.Z, 1f);
        }
    }

    public static readonly Transform Identity = new()
    {
        Position = Vector3.Zero,
        Rotation = Quaternion.Identity,
        Scale = Vector3.One,
    };
}
