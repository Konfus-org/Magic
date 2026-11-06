using System.Numerics;

namespace Magic.Ecs.Components;

/// <summary>
/// Turns its entity's <see cref="Transform"/> in place at a steady rate, in the world's time, so it stands still while
/// the world is paused. A new one turns about +Y, once a minute.
/// </summary>
public record struct Spin : IComponent
{
    /// <summary>
    /// Degrees a second; positive turns as a left-handed rotation about <see cref="Axis"/> does.
    /// </summary>
    public float DegreesPerSec = 6;

    /// <summary>
    /// What it turns about, in the space its transform is in; it needn't be unit length.
    /// </summary>
    public Vector3 Axis = Vector3.UnitY;

    public Spin() { }

    /// <summary>
    /// <paramref name="rotation"/> turned on by <paramref name="deltaSec"/> of this spin; unchanged for an axis of no
    /// length.
    /// </summary>
    public readonly Quaternion Turn(Quaternion rotation, float deltaSec)
    {
        float length = Axis.Length();
        if (length <= 0)
            return rotation;

        float angle = DegreesPerSec * deltaSec * MathF.PI / 180;
        Quaternion turn = Quaternion.CreateFromAxisAngle(Axis / length, angle);

        return Quaternion.Normalize(Quaternion.Concatenate(rotation, turn));
    }
}
