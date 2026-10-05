using Magic.Extensions;
using System.Drawing;
using System.Numerics;

namespace DeferredRendererGem;

/// <summary>
/// One cascade of the sun's shadow as fitted: a sphere around one slice of the camera's frustum, fixed in the world and
/// snapped to its own texel grid, and the orthographic volume around it that the casters are drawn in.
/// </summary>
internal readonly record struct Cascade(Vector3 Center, float Radius, float TexelWorld, float FarSplit, Matrix4x4 LightRotation, float NearPlane, float FarPlane)
{
    /// <summary>
    /// The light's view-projection for positions relative to <paramref name="origin"/>: the cascade's centre for its own
    /// shadow view, the camera's position for the lighting that reads it.
    /// </summary>
    public Matrix4x4 ViewProj(Vector3 origin)
    {
        return Matrix4x4.CreateTranslation(origin - Center) * LightRotation * Matrix4x4.OrthographicReverseZ(2f * Radius, 2f * Radius, NearPlane, FarPlane);
    }
}

/// <summary>
/// The sun's cascades, as maths: where the view is split, how a slice is fitted into a stable sphere, and the light's
/// view. No GPU and no state, so every rule here is a unit test.
/// </summary>
internal static class Cascades
{
    public const int MaxCascades = 4;

    /// <summary>
    /// The far edge of each cascade, the practical split scheme: <paramref name="lambda"/> 0 is even steps from
    /// <paramref name="near"/> to <paramref name="distance"/>, 1 is logarithmic steps, between a blend. The last is the distance.
    /// </summary>
    public static void Split(float near, float distance, float lambda, Span<float> fars)
    {
        int count = fars.Length;
        for (int i = 1; i <= count; i++)
        {
            float share = (float)i / count;
            float uniform = near + ((distance - near) * share);
            float logarithmic = near > 0f ? near * MathF.Pow(distance / near, share) : uniform;
            fars[i - 1] = float.Lerp(uniform, logarithmic, lambda);
        }

        fars[count - 1] = distance;
    }

    /// <summary>
    /// The rotation-only view of a light shining along <paramref name="direction"/>: like <see cref="Magic.Contexts.Components.Camera.ViewMatrix"/>
    /// for a camera looking that way, with up the world axis least along it.
    /// </summary>
    public static Matrix4x4 LightRotation(Vector3 direction)
    {
        Vector3 forward = Vector3.Normalize(direction);
        Vector3 up = MathF.Abs(forward.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
        Vector3 right = Vector3.Normalize(Vector3.Cross(up, forward));
        up = Vector3.Cross(forward, right);

        return new Matrix4x4(
            right.X, up.X, forward.X, 0f,
            right.Y, up.Y, forward.Y, 0f,
            right.Z, up.Z, forward.Z, 0f,
            0f, 0f, 0f, 1f);
    }

    /// <summary>
    /// Fits the slice of a perspective camera's frustum between two view depths: the sphere through the slice's corners,
    /// its radius rounded up so it is the same every frame, its centre moved onto the light-space texel grid of
    /// <paramref name="resolution"/> texels across, so rotating or moving the camera never shifts a texel's footprint
    /// by a fraction (no shimmer). The volume reaches <paramref name="casterRange"/> towards the light past the sphere.
    /// </summary>
    public static Cascade Fit(in Matrix4x4 cameraWorld, float fovYRadians, float aspect, float nearSplit, float farSplit, in Matrix4x4 lightRotation, int resolution, float casterRange)
    {
        // A corner at depth d is d * sqrt(1 + a2) from the camera; the centre on the axis equidistant from the near
        // and the far corners, never past the far slice.
        float tanHalf = MathF.Tan(fovYRadians * 0.5f);
        float a2 = tanHalf * tanHalf * (1f + (aspect * aspect));
        float along = MathF.Min(farSplit, 0.5f * (1f + a2) * (nearSplit + farSplit));
        float radius = MathF.Sqrt(((farSplit - along) * (farSplit - along)) + (a2 * farSplit * farSplit));
        radius = MathF.Ceiling(radius * 16f) / 16f;
        float texel = 2f * radius / resolution;

        Vector3 forward = Vector3.Normalize(cameraWorld.Forward);
        Vector3 center = cameraWorld.Translation + (forward * along);

        // Snapped in light space, in absolute world coordinates: the grid is fixed in the world, not to the camera.
        Vector3 inLight = Vector3.Transform(center, lightRotation);
        inLight.X = MathF.Floor(inLight.X / texel) * texel;
        inLight.Y = MathF.Floor(inLight.Y / texel) * texel;
        Vector3 snapped = Vector3.Transform(inLight, Matrix4x4.Transpose(lightRotation));

        return new Cascade(snapped, radius, texel, farSplit, lightRotation, -(radius + casterRange), radius);
    }

    /// <summary>
    /// The planes of the camera's frustum slice swept towards the sun (the direction its light comes from): what a
    /// caster must touch to shadow anything the camera sees of the slice. The swept volume is bounded by the slice's
    /// faces that face away from the sweep and by a plane through every silhouette edge (an edge between a kept face
    /// and a dropped one) along the sweep; it is open towards the sun. Relative to <paramref name="eye"/>, normals
    /// pointing in, as many as there are (at most <see cref="MaxReceiverPlanes"/>).
    /// </summary>
    public static int ReceiverPlanes(in Matrix4x4 cameraWorld, float fovYRadians, float aspect, float nearSplit, float farSplit, Vector3 sunDirection, Vector3 eye, Span<Vector4> planes)
    {
        Span<Vector3> corners = stackalloc Vector3[8];
        SliceCorners(cameraWorld, fovYRadians, aspect, nearSplit, farSplit, corners);
        Vector3 centroid = Vector3.Zero;
        foreach (Vector3 corner in corners)
            centroid += corner / 8f;
        Vector3 sweep = -Vector3.Normalize(sunDirection);

        // Each face by three of its corners (SliceCorners' order: near +r+u, -r+u, +r-u, -r-u, then the far four), and
        // each edge by the two faces it lies between.
        ReadOnlySpan<(int A, int B, int C)> faces = [(0, 1, 2), (4, 6, 5), (0, 2, 4), (1, 5, 3), (0, 4, 1), (2, 3, 6)];
        ReadOnlySpan<(int A, int B, int Face0, int Face1)> edges =
        [
            (0, 1, 0, 4), (1, 3, 0, 3), (3, 2, 0, 5), (2, 0, 0, 2),
            (4, 5, 1, 4), (5, 7, 1, 3), (7, 6, 1, 5), (6, 4, 1, 2),
            (0, 4, 4, 2), (1, 5, 4, 3), (2, 6, 5, 2), (3, 7, 5, 3),
        ];

        Span<bool> kept = stackalloc bool[6];
        int count = 0;
        for (int face = 0; face < faces.Length; face++)
        {
            (int a, int b, int c) = faces[face];
            Vector3 normal = Inward(Vector3.Cross(corners[b] - corners[a], corners[c] - corners[a]), corners[a], centroid);
            kept[face] = Vector3.Dot(normal, sweep) >= -1e-4f;
            if (kept[face])
                planes[count++] = new Vector4(normal, Vector3.Dot(normal, eye - corners[a]));
        }

        foreach ((int a, int b, int face0, int face1) in edges)
        {
            if (kept[face0] == kept[face1])
                continue;

            Vector3 along = Vector3.Cross(corners[b] - corners[a], sweep);
            if (along.LengthSquared() < 1e-12f)
                continue;

            Vector3 normal = Inward(along, corners[a], centroid);
            planes[count++] = new Vector4(normal, Vector3.Dot(normal, eye - corners[a]));
        }

        return count;
    }

    /// <summary>
    /// A plane through two points on it and the sweep: at most six faces and six silhouette edges of a box.
    /// </summary>
    public const int MaxReceiverPlanes = 12;

    private static Vector3 Inward(Vector3 normal, Vector3 onPlane, Vector3 inside)
    {
        normal = Vector3.Normalize(normal);
        return Vector3.Dot(normal, inside - onPlane) < 0f ? -normal : normal;
    }

    /// <summary>
    /// The depth of the camera's view a point along its forward axis sits at.
    /// </summary>
    public static float ViewDepth(in Matrix4x4 cameraWorld, Vector3 point)
    {
        return Vector3.Dot(point - cameraWorld.Translation, Vector3.Normalize(cameraWorld.Forward));
    }

    /// <summary>
    /// The eight corners of a perspective frustum slice, in the world.
    /// </summary>
    public static void SliceCorners(in Matrix4x4 cameraWorld, float fovYRadians, float aspect, float nearSplit, float farSplit, Span<Vector3> corners)
    {
        Vector3 forward = Vector3.Normalize(cameraWorld.Forward);
        Vector3 right = Vector3.Normalize(cameraWorld.Right);
        Vector3 up = Vector3.Normalize(cameraWorld.Up);
        float tanHalf = MathF.Tan(fovYRadians * 0.5f);
        int next = 0;
        foreach (float depth in (ReadOnlySpan<float>)[nearSplit, farSplit])
        {
            float halfHeight = depth * tanHalf;
            float halfWidth = halfHeight * aspect;
            Vector3 middle = cameraWorld.Translation + (forward * depth);
            corners[next++] = middle + (right * halfWidth) + (up * halfHeight);
            corners[next++] = middle - (right * halfWidth) + (up * halfHeight);
            corners[next++] = middle + (right * halfWidth) - (up * halfHeight);
            corners[next++] = middle - (right * halfWidth) - (up * halfHeight);
        }
    }

    /// <summary>
    /// Where cascade <paramref name="index"/> of the view at <paramref name="row"/> sits in the atlas.
    /// </summary>
    public static Rectangle Tile(int index, int row, int resolution)
    {
        return new Rectangle(index * resolution, row * resolution, resolution, resolution);
    }
}
