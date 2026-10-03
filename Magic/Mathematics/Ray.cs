using System.Numerics;

namespace Magic.Mathematics;

/// <summary>
/// A half line. <see cref="Direction"/> is unit length.
/// </summary>
public readonly record struct Ray(Vector3 Origin, Vector3 Direction)
{
    public Vector3 At(float t)
    {
        return Origin + (Direction * t);
    }

    /// <summary>
    /// Distance along the ray to the sphere, or null when it misses (a ray starting inside hits at 0).
    /// </summary>
    public float? Intersect(in BoundingSphere sphere)
    {
        Vector3 oc = Origin - sphere.Center;
        float b = Vector3.Dot(oc, Direction);
        float c = oc.LengthSquared() - (sphere.Radius * sphere.Radius);

        if (c <= 0f)
            return 0f;

        float discriminant = (b * b) - c;
        if (discriminant < 0f)
            return null;

        float t = -b - MathF.Sqrt(discriminant);
        return t >= 0f ? t : null;
    }

    /// <summary>
    /// Distance along the ray to the box (slab test), or null when it misses (a ray starting inside hits at 0).
    /// A zero direction component makes that slab ±infinity, which is either everything or nothing; the one
    /// NaN case, 0 * infinity when the origin lies exactly on a face it runs parallel to, is inside the slab.
    /// </summary>
    public float? Intersect(in Aabb box)
    {
        Vector3 inv = Vector3.One / Direction;
        Vector3 t0 = (box.Min - Origin) * inv;
        Vector3 t1 = (box.Max - Origin) * inv;
        Vector3 onFace = Vector3.IsNaN(t0) | Vector3.IsNaN(t1);
        Vector3 near = Vector3.ConditionalSelect(onFace, new Vector3(float.NegativeInfinity), Vector3.Min(t0, t1));
        Vector3 far = Vector3.ConditionalSelect(onFace, new Vector3(float.PositiveInfinity), Vector3.Max(t0, t1));
        float tMin = MathF.Max(MathF.Max(0f, near.X), MathF.Max(near.Y, near.Z));
        float tMax = MathF.Min(far.X, MathF.Min(far.Y, far.Z));

        if (tMin > tMax)
            return null;

        return tMin;
    }
}
