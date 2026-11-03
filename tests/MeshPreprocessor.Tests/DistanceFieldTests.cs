using System.Numerics;
using Xunit;

namespace MeshPreprocessor.Tests;

/// <summary>
/// The distance field of a unit cube: exact at the voxels nearest its centre and faces, negative inside, positive
/// outside, and no farther than the band anywhere.
/// </summary>
public sealed class DistanceFieldTests
{
    [Fact]
    public void CubeIsNegativeInsideAndPositiveOutside()
    {
        DistanceField field = DistanceFieldBuilder.Build(Cube.Positions, Cube.Triangles);
        Assert.True(Sample(field, Vector3.Zero) < 0, "the centre reads outside");
        Assert.True(Sample(field, new Vector3(0.55f, 0, 0)) > 0, "beside the cube reads inside");
        Assert.True(Sample(field, new Vector3(0, 0.55f, 0.55f)) > 0, "past the edge reads inside");
        Assert.True(Sample(field, new Vector3(-0.56f, -0.56f, -0.56f)) > 0, "past the far corner reads inside");
    }

    [Fact]
    public void CubeDistancesAreTheirMetresWithinTheBand()
    {
        DistanceField field = DistanceFieldBuilder.Build(Cube.Positions, Cube.Triangles);
        float tolerance = field.Voxel;
        Assert.InRange(Sample(field, new Vector3(0.5f, 0, 0)), -tolerance, tolerance);
        Assert.InRange(Sample(field, new Vector3(0.5f + field.Voxel, 0, 0)), 0, field.Voxel + tolerance);
        Assert.InRange(Sample(field, new Vector3(0.5f - field.Voxel, 0, 0)), -field.Voxel - tolerance, 0);
        float centre = Sample(field, Vector3.Zero);
        Assert.InRange(centre, -Math.Min(0.5f, field.Band) - tolerance, -Math.Min(0.5f, field.Band) + tolerance);
    }

    [Fact]
    public void FieldNeverExceedsTheBand()
    {
        DistanceField field = DistanceFieldBuilder.Build(Cube.Positions, Cube.Triangles);
        foreach (byte voxel in field.Voxels)
        {
            Assert.InRange(unchecked((sbyte)voxel), -127, 127);
        }

        Assert.Equal(
            DistanceFieldBuilder.Size * DistanceFieldBuilder.Size * DistanceFieldBuilder.Size,
            field.Voxels.Length
        );
    }

    /// <summary>
    /// The field's distance at the voxel nearest a point, in metres.
    /// </summary>
    private static float Sample(DistanceField field, Vector3 point)
    {
        Vector3 cell = (point - field.Origin) / field.Voxel;
        int x = (int)MathF.Round(cell.X);
        int y = (int)MathF.Round(cell.Y);
        int z = (int)MathF.Round(cell.Z);
        int size = DistanceFieldBuilder.Size;
        sbyte value = unchecked((sbyte)field.Voxels[(((z * size) + y) * size) + x]);

        return value / 127f * field.Band;
    }

    /// <summary>
    /// A unit cube centred on the origin, its triangles clockwise from outside, as the engine's models are.
    /// </summary>
    private static class Cube
    {
        public static readonly Vector3[] Positions =
        [
            new(-0.5f, -0.5f, -0.5f),
            new(0.5f, -0.5f, -0.5f),
            new(0.5f, 0.5f, -0.5f),
            new(-0.5f, 0.5f, -0.5f),
            new(-0.5f, -0.5f, 0.5f),
            new(0.5f, -0.5f, 0.5f),
            new(0.5f, 0.5f, 0.5f),
            new(-0.5f, 0.5f, 0.5f),
        ];

        public static readonly (int A, int B, int C)[] Triangles =
        [
            (0, 2, 1),
            (0, 3, 2),
            (4, 5, 6),
            (4, 6, 7),
            (0, 1, 5),
            (0, 5, 4),
            (3, 7, 6),
            (3, 6, 2),
            (0, 4, 7),
            (0, 7, 3),
            (1, 2, 6),
            (1, 6, 5),
        ];
    }
}
