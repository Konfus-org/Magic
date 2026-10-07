using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Magic.Mathematics;

/// <summary>
/// The six planes of a view volume, normals pointing inwards, extracted from a row-vector view-projection
/// (Gribb and Hartmann). Reverse-Z aware: the near plane (depth 1) is <c>w - z ≥ 0</c> and the far plane
/// (depth 0) is <c>z ≥ 0</c>, which for an infinite projection means "in front of the camera". A point is
/// inside when every plane's signed distance is ≥ 0; a sphere when every distance is ≥ -r.
/// </summary>
public readonly struct Frustum
{
    private readonly PlaneArray _planes;

    private Frustum(Plane left, Plane right, Plane bottom, Plane top, Plane near, Plane far)
    {
        _planes[0] = left;
        _planes[1] = right;
        _planes[2] = bottom;
        _planes[3] = top;
        _planes[4] = near;
        _planes[5] = far;
    }

    /// <summary>
    /// The planes in the order the GPU culler expects them: left, right, bottom, top, near, far. The span
    /// points into this frustum, so it lives no longer than the variable it was read from.
    /// </summary>
    public ReadOnlySpan<Plane> Planes => MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in _planes[0]), PlaneArray.Length);

    public static Frustum FromViewProjection(in Matrix4x4 m)
    {
        // Rows of the row-vector matrix are the columns of the column-vector one the derivation is written for.
        Vector4 c0 = new(m.M11, m.M21, m.M31, m.M41);
        Vector4 c1 = new(m.M12, m.M22, m.M32, m.M42);
        Vector4 c2 = new(m.M13, m.M23, m.M33, m.M43);
        Vector4 c3 = new(m.M14, m.M24, m.M34, m.M44);

        return new Frustum(
            Normalize(c3 + c0),   // left:   w + x ≥ 0
            Normalize(c3 - c0),   // right:  w - x ≥ 0
            Normalize(c3 + c1),   // bottom: w + y ≥ 0
            Normalize(c3 - c1),   // top:    w - y ≥ 0
            Normalize(c3 - c2),   // near (reverse-Z, depth ≤ 1): w - z ≥ 0
            Normalize(c2));       // far (reverse-Z, depth ≥ 0): z ≥ 0
    }

    public bool Intersects(in BoundingSphere sphere)
    {
        float r = -sphere.Radius;
        foreach (Plane plane in _planes)
        {
            if (Plane.DotCoordinate(plane, sphere.Center) < r)
                return false;
        }

        return true;
    }

    public bool Intersects(in Aabb box)
    {
        foreach (Plane plane in _planes)
        {
            if (Outside(plane, box))
                return false;
        }

        return true;
    }

    public bool Contains(Vector3 point)
    {
        foreach (Plane plane in _planes)
        {
            if (Plane.DotCoordinate(plane, point) < 0)
                return false;
        }

        return true;
    }

    /// <summary>
    /// True when the box is wholly on the outside of the plane (the p-vertex test).
    /// </summary>
    private static bool Outside(in Plane plane, in Aabb box)
    {
        Vector3 p = new(
            plane.Normal.X >= 0 ? box.Max.X : box.Min.X,
            plane.Normal.Y >= 0 ? box.Max.Y : box.Min.Y,
            plane.Normal.Z >= 0 ? box.Max.Z : box.Min.Z);

        return Plane.DotCoordinate(plane, p) < 0;
    }

    private static Plane Normalize(Vector4 p)
    {
        float length = MathF.Sqrt((p.X * p.X) + (p.Y * p.Y) + (p.Z * p.Z));

        if (length > 1e-12f)
            return new Plane(p.X / length, p.Y / length, p.Z / length, p.W / length);

        // An infinite projection's far plane has no normal (0 * z + near >= 0): everything passes it.
        return new Plane(0f, 0f, 0f, float.MaxValue);
    }

    [InlineArray(Length)]
    private struct PlaneArray
    {
        public const int Length = 6;

        private Plane _element;
    }
}
