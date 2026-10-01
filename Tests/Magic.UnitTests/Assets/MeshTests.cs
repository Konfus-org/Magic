using Magic.Contexts.Assets;
using Magic.Mathematics;
using System.Numerics;
using Xunit;

namespace Magic.UnitTests.Assets;

public sealed class MeshTests
{
    [Fact]
    public void The_box_spans_the_vertices()
    {
        Mesh mesh = Corners();

        mesh.ComputeBounds();

        Assert.Equal(new Aabb(new Vector3(-1, -2, -3), new Vector3(1, 2, 3)), mesh.Box);
    }

    [Fact]
    public void The_bounding_sphere_passes_through_the_corners_of_the_box()
    {
        Mesh mesh = Corners();

        mesh.ComputeBounds();

        Assert.Equal(MathF.Sqrt(14f), mesh.Bounds.Radius, 1e-5f);
    }

    private static Mesh Corners()
    {
        return new Mesh
        {
            Vertices =
            [
                new Vertex { Position = new Vector3(-1, -2, -3) },
                new Vertex { Position = new Vector3(1, 2, 3) },
                new Vertex { Position = Vector3.Zero },
            ],
        };
    }
}
