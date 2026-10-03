using System.Numerics;
using System.Text.Json.Serialization;

namespace Magic.Mathematics;

/// <summary>
/// An axis-aligned box. <see cref="Min"/> ≤ <see cref="Max"/> per axis; the default is empty at the origin.
/// </summary>
public readonly record struct Aabb(Vector3 Min, Vector3 Max)
{
    [JsonIgnore]
    public Vector3 Center => (Min + Max) * 0.5f;

    /// <summary>
    /// Half the size per axis.
    /// </summary>
    [JsonIgnore]
    public Vector3 Extents => (Max - Min) * 0.5f;

    /// <summary>
    /// The sphere through the box's corners: centred on it, radius half its diagonal.
    /// </summary>
    [JsonIgnore]
    public BoundingSphere Sphere => new(Center, Extents.Length());

    public static Aabb FromPoints(ReadOnlySpan<Vector3> points)
    {
        if (points.IsEmpty)
            return default;

        Vector3 min = points[0], max = points[0];
        foreach (Vector3 p in points)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        return new Aabb(min, max);
    }

    /// <summary>
    /// The box around this box's eight transformed corners (Arvo's method: no corner loop).
    /// </summary>
    public Aabb Transform(in Matrix4x4 m)
    {
        Vector3 center = Vector3.Transform(Center, m);
        Vector3 e = Extents;
        Vector3 extents = new(
            (MathF.Abs(m.M11) * e.X) + (MathF.Abs(m.M21) * e.Y) + (MathF.Abs(m.M31) * e.Z),
            (MathF.Abs(m.M12) * e.X) + (MathF.Abs(m.M22) * e.Y) + (MathF.Abs(m.M32) * e.Z),
            (MathF.Abs(m.M13) * e.X) + (MathF.Abs(m.M23) * e.Y) + (MathF.Abs(m.M33) * e.Z));

        return new Aabb(center - extents, center + extents);
    }

    public Aabb Union(in Aabb other)
    {
        return new Aabb(Vector3.Min(Min, other.Min), Vector3.Max(Max, other.Max));
    }

    public bool Contains(Vector3 p)
    {
        return p.X >= Min.X && p.Y >= Min.Y && p.Z >= Min.Z && p.X <= Max.X && p.Y <= Max.Y && p.Z <= Max.Z;
    }

    /// <summary>
    /// The squared distance from <paramref name="p"/> to the closest point of the box; 0 inside.
    /// </summary>
    public float DistanceSquared(Vector3 p)
    {
        Vector3 closest = Vector3.Clamp(p, Min, Max);
        return Vector3.DistanceSquared(p, closest);
    }

    public bool Intersects(in Aabb other)
    {
        return Min.X <= other.Max.X && Max.X >= other.Min.X
            && Min.Y <= other.Max.Y && Max.Y >= other.Min.Y
            && Min.Z <= other.Max.Z && Max.Z >= other.Min.Z;
    }
}
