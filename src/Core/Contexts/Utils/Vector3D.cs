using System.Numerics;

namespace Magic.Contexts.Utils;

/// <summary>
/// A point or a direction in double precision, in metres: where an entity is (<see cref="Components.Transform.Position"/>).
/// Doubles hold a position to a tenth of a millimetre out past the Sun's distance from the origin, where floats are a
/// millimetre out by 8 km, so a world has no edge to its precision. What is near something else is worked out as the difference,
/// which is small, and turned to floats only then (<see cref="ToVector3"/>), as the GPU and shaders take it.
/// </summary>
public record struct Vector3D
{
    public double X;

    public double Y;

    public double Z;

    public Vector3D(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public readonly double Length => Math.Sqrt(Dot(this, this));

    public static implicit operator Vector3D(Vector3 vector) => FromVector3(vector);

    public static Vector3D operator +(Vector3D left, Vector3D right) => Add(left, right);

    public static Vector3D operator -(Vector3D left, Vector3D right) => Subtract(left, right);

    public static Vector3D operator *(Vector3D vector, double scale) => Multiply(vector, scale);

    public static Vector3D operator *(Vector3D vector, Vector3 scale) => Multiply(vector, scale);

    public static Vector3D FromVector3(Vector3 vector) => new(vector.X, vector.Y, vector.Z);

    public static Vector3D Add(Vector3D left, Vector3D right) => new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);

    public static Vector3D Subtract(Vector3D left, Vector3D right) => new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);

    public static Vector3D Multiply(Vector3D vector, double scale) => new(vector.X * scale, vector.Y * scale, vector.Z * scale);

    /// <summary>
    /// Each axis scaled by its own: a part's offset by its parent's scale.
    /// </summary>
    public static Vector3D Multiply(Vector3D vector, Vector3 scale) => new(vector.X * scale.X, vector.Y * scale.Y, vector.Z * scale.Z);

    public static double Dot(Vector3D left, Vector3D right) => (left.X * right.X) + (left.Y * right.Y) + (left.Z * right.Z);

    public static Vector3D Cross(Vector3D left, Vector3D right) => new((left.Y * right.Z) - (left.Z * right.Y), (left.Z * right.X) - (left.X * right.Z), (left.X * right.Y) - (left.Y * right.X));

    public static double Distance(Vector3D left, Vector3D right) => (left - right).Length;

    public static Vector3D Min(Vector3D left, Vector3D right) => new(Math.Min(left.X, right.X), Math.Min(left.Y, right.Y), Math.Min(left.Z, right.Z));

    public static Vector3D Max(Vector3D left, Vector3D right) => new(Math.Max(left.X, right.X), Math.Max(left.Y, right.Y), Math.Max(left.Z, right.Z));

    /// <summary>
    /// <paramref name="vector"/> turned by <paramref name="rotation"/>, in double precision: v + 2w(q × v) + 2q × (q × v).
    /// </summary>
    public static Vector3D Transform(Vector3D vector, Quaternion rotation)
    {
        Vector3D axis = new(rotation.X, rotation.Y, rotation.Z);
        Vector3D twice = Cross(axis, vector) * 2;
        return vector + (twice * rotation.W) + Cross(axis, twice);
    }

    /// <summary>
    /// Of length 1 the same way, or itself when it has no length.
    /// </summary>
    public readonly Vector3D Normalized()
    {
        double length = Length;
        return length > 0 ? this * (1 / length) : this;
    }

    /// <summary>
    /// In floats: for a difference between nearby points, or a direction, never a far position, which floats round.
    /// </summary>
    public readonly Vector3 ToVector3() => new((float)X, (float)Y, (float)Z);
}
