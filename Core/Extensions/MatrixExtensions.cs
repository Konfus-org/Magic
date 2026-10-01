using System.Numerics;

namespace Magic.Extensions;

/// <summary>
/// The matrices the engine uses, in System.Numerics' row-vector convention (<c>v * M</c>, translation in the
/// last row). Every projection is reverse-Z: depth 1 at the near plane, falling towards 0 with distance, so a
/// float depth buffer keeps its precision where the scene is. The BCL's left-handed helpers throw for
/// near ≥ far, which an infinite far plane is, hence these.
/// </summary>
public static class MatrixExtensions
{
    extension(Matrix4x4 matrix)
    {
        /// <summary>The first row: the local +X axis in world space, scale included.</summary>
        public Vector3 Right => new(matrix.M11, matrix.M12, matrix.M13);

        /// <summary>The second row: the local +Y axis in world space, scale included.</summary>
        public Vector3 Up => new(matrix.M21, matrix.M22, matrix.M23);

        /// <summary>The third row: the local +Z axis in world space, scale included.</summary>
        public Vector3 Forward => new(matrix.M31, matrix.M32, matrix.M33);

        /// <summary>The length of the longest axis: what a sphere's radius scales by, conservatively for non-uniform scale.</summary>
        public float MaxScale => MathF.Sqrt(MathF.Max(matrix.Right.LengthSquared(), MathF.Max(matrix.Up.LengthSquared(), matrix.Forward.LengthSquared())));
    }

    extension(Matrix4x4)
    {
        /// <summary>
        /// Perspective, reverse-Z: 1 at <paramref name="near"/>, 0 at <paramref name="far"/>. An infinite far
        /// plane (the default) never clips anything for being distant; depth is then <c>near / z</c>.
        /// </summary>
        public static Matrix4x4 PerspectiveReverseZ(float fovYRadians, float aspect, float near, float far = float.PositiveInfinity)
        {
            float h = 1f / MathF.Tan(fovYRadians * 0.5f);

            if (float.IsPositiveInfinity(far))
            {
                return new Matrix4x4(
                    h / aspect, 0f, 0f, 0f,
                    0f, h, 0f, 0f,
                    0f, 0f, 0f, 1f,
                    0f, 0f, near, 0f);
            }

            float range = far - near;
            return new Matrix4x4(
                h / aspect, 0f, 0f, 0f,
                0f, h, 0f, 0f,
                0f, 0f, -near / range, 1f,
                0f, 0f, near * far / range, 0f);
        }

        /// <summary>Orthographic, reverse-Z: 1 at <paramref name="near"/>, 0 at <paramref name="far"/>.</summary>
        public static Matrix4x4 OrthographicReverseZ(float width, float height, float near, float far)
        {
            float range = far - near;
            return new Matrix4x4(
                2f / width, 0f, 0f, 0f,
                0f, 2f / height, 0f, 0f,
                0f, 0f, -1f / range, 0f,
                0f, 0f, far / range, 1f);
        }

        /// <summary>
        /// Scale, then rotate, then translate: the same result as
        /// <c>CreateScale * CreateFromQuaternion * CreateTranslation</c>, built without the two 4x4 multiplies.
        /// Every entity's world matrix comes through here each frame.
        /// </summary>
        public static Matrix4x4 Trs(in Vector3 position, in Quaternion rotation, in Vector3 scale)
        {
            float xx = rotation.X * rotation.X, yy = rotation.Y * rotation.Y, zz = rotation.Z * rotation.Z;
            float xy = rotation.X * rotation.Y, xz = rotation.X * rotation.Z, yz = rotation.Y * rotation.Z;
            float wx = rotation.W * rotation.X, wy = rotation.W * rotation.Y, wz = rotation.W * rotation.Z;

            return new Matrix4x4(
                scale.X * (1f - (2f * (yy + zz))), scale.X * 2f * (xy + wz), scale.X * 2f * (xz - wy), 0f,
                scale.Y * 2f * (xy - wz), scale.Y * (1f - (2f * (xx + zz))), scale.Y * 2f * (yz + wx), 0f,
                scale.Z * 2f * (xz + wy), scale.Z * 2f * (yz - wx), scale.Z * (1f - (2f * (xx + yy))), 0f,
                position.X, position.Y, position.Z, 1f);
        }
    }
}
