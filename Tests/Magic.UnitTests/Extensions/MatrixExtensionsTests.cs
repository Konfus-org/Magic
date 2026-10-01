using Magic.Extensions;
using System.Numerics;
using Xunit;

namespace Magic.UnitTests.Extensions;

public sealed class MatrixExtensionsTests
{
    [Theory]
    [InlineData(0.1f, 1f)]  // near
    [InlineData(1f, 0.1f)]  // 1/z falloff
    public void Infinite_reverse_z_maps_depth_to_near_over_z(float z, float depth)
    {
        Matrix4x4 matrix = Matrix4x4.PerspectiveReverseZ(float.DegreesToRadians(60f), 16f / 9f, 0.1f);

        float projected = Depth(matrix, z);

        Assert.Equal(depth, projected, 1e-5f);
    }

    [Fact]
    public void Infinite_reverse_z_stays_finite_at_extreme_distance()
    {
        Matrix4x4 matrix = Matrix4x4.PerspectiveReverseZ(float.DegreesToRadians(60f), 16f / 9f, 0.1f);

        float projected = Depth(matrix, 1e9f);

        Assert.True(float.IsFinite(projected));
    }

    [Theory]
    [InlineData(0.5f, 1f)]
    [InlineData(100f, 0f)]
    public void Finite_reverse_z_maps_near_to_one_and_far_to_zero(float z, float depth)
    {
        Matrix4x4 matrix = Matrix4x4.PerspectiveReverseZ(1f, 1f, 0.5f, 100f);

        float projected = Depth(matrix, z);

        Assert.Equal(depth, projected, 1e-5f);
    }

    [Theory]
    [InlineData(1f, 1f)]
    [InlineData(50f, 0f)]
    public void Orthographic_reverse_z_maps_near_to_one_and_far_to_zero(float z, float depth)
    {
        Matrix4x4 matrix = Matrix4x4.OrthographicReverseZ(20f, 10f, 1f, 50f);

        float projected = Depth(matrix, z);

        Assert.Equal(depth, projected, 1e-5f);
    }

    [Fact]
    public void Orthographic_maps_half_the_width_to_the_clip_edge()
    {
        Matrix4x4 matrix = Matrix4x4.OrthographicReverseZ(20f, 10f, 1f, 50f);

        Vector4 clip = Vector4.Transform(new Vector4(10f, 0f, 10f, 1f), matrix);

        Assert.Equal(1f, clip.X, 1e-5f);
    }

    [Fact]
    public void Trs_scales_then_rotates_then_translates()
    {
        Quaternion quarterTurnAboutY = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
        Matrix4x4 matrix = Matrix4x4.Trs(new Vector3(10, 0, 0), quarterTurnAboutY, new Vector3(2, 1, 1));

        Vector3 moved = Vector3.Transform(Vector3.UnitX, matrix);

        // (1, 0, 0) scaled to (2, 0, 0), turned to (0, 0, -2), moved to (10, 0, -2).
        Assert.Equal(0f, Vector3.Distance(new Vector3(10, 0, -2), moved), 1e-5f);
    }

    [Fact]
    public void Max_scale_is_the_length_of_the_longest_axis()
    {
        Matrix4x4 matrix = Matrix4x4.Trs(Vector3.Zero, Quaternion.Identity, new Vector3(2, 5, 3));

        float scale = matrix.MaxScale;

        Assert.Equal(5f, scale, 1e-5f);
    }

    private static float Depth(in Matrix4x4 matrix, float z)
    {
        Vector4 clip = Vector4.Transform(new Vector4(0f, 0f, z, 1f), matrix);

        return clip.Z / clip.W;
    }
}
