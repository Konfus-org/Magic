using System.Numerics;
using Magic.Files.Types;
using Magic.Rendering;
using Xunit;

namespace Magic.MeshPreprocessor.Tests;

/// <summary>
/// The hierarchy of a sphere of a few thousand triangles: leaves of its own triangles, errors that never fall going
/// up, and a root of a dozen triangles whose parent error is the model's diameter.
/// </summary>
public sealed class ClusterBuilderTests
{
    [Fact]
    public void LeavesHoldEveryTriangleAtNoError()
    {
        Model model = Sphere.Build(48, 24);
        MeshDerived derived = ClusterBuilder.Build(model);
        int leafTriangles = 0;
        foreach (GpuCluster cluster in derived.Clusters)
        {
            if (cluster.Error == 0)
                leafTriangles += (int)cluster.TriangleCount;

            Assert.InRange(cluster.TriangleCount, 1u, (uint)GpuCluster.MaxTriangles);
        }

        Assert.Equal(model.Meshes[0].Indices.Length / 3, leafTriangles);
    }

    [Fact]
    public void ErrorsNeverFallGoingUp()
    {
        MeshDerived derived = ClusterBuilder.Build(Sphere.Build(48, 24));
        foreach (GpuCluster cluster in derived.Clusters)
        {
            Assert.True(cluster.ParentError >= cluster.Error, "a parent's error is below its child's");
            Assert.True(cluster.ParentRadius >= cluster.DetailRadius * 0.999f, "a parent's sphere is smaller");
        }
    }

    [Fact]
    public void RootIsADozenTrianglesUnderTheDiameter()
    {
        MeshDerived derived = ClusterBuilder.Build(Sphere.Build(48, 24));
        int rootTriangles = 0;
        int roots = 0;
        foreach (GpuCluster cluster in derived.Clusters)
        {
            if (cluster.ParentCenter != derived.BoundsCenter || cluster.ParentError != 2 * cluster.ParentRadius)
                continue;

            roots++;
            rootTriangles += (int)cluster.TriangleCount;
            Assert.InRange(cluster.ParentRadius, derived.BoundsRadius, derived.BoundsRadius * 2);
            Assert.True(cluster.Error > 0, "the root is drawn as if it were exact");
        }

        Assert.Equal(1, roots);
        Assert.InRange(rootTriangles, 1, ClusterBuilder.RootTriangles);
        Assert.InRange(derived.BoundsRadius, 0.99f, 1.01f);
    }

    /// <summary>
    /// A unit sphere of rings and segments, its triangles clockwise from outside, as the engine's models are.
    /// </summary>
    private static class Sphere
    {
        public static Model Build(int segments, int rings)
        {
            List<Vertex> vertices = [];
            for (int ring = 0; ring <= rings; ring++)
            {
                float pitch = MathF.PI * ring / rings;
                for (int segment = 0; segment <= segments; segment++)
                {
                    float yaw = 2 * MathF.PI * segment / segments;
                    Vector3 normal = new(
                        MathF.Sin(pitch) * MathF.Cos(yaw),
                        MathF.Cos(pitch),
                        MathF.Sin(pitch) * MathF.Sin(yaw)
                    );
                    vertices.Add(
                        new Vertex
                        {
                            Position = normal,
                            Normal = normal,
                            Tangent = new Vector4(-MathF.Sin(yaw), 0, MathF.Cos(yaw), 1),
                            Uv = new Vector2((float)segment / segments, (float)ring / rings),
                        }
                    );
                }
            }

            List<uint> indices = [];
            for (int ring = 0; ring < rings; ring++)
            {
                for (int segment = 0; segment < segments; segment++)
                {
                    uint a = (uint)((ring * (segments + 1)) + segment);
                    uint b = a + (uint)segments + 1;
                    if (ring > 0)
                        indices.AddRange([a, a + 1, b]);

                    if (ring < rings - 1)
                        indices.AddRange([a + 1, b + 1, b]);
                }
            }

            return new Model { Meshes = [new Mesh { Vertices = vertices.ToArray(), Indices = indices.ToArray() }] };
        }
    }
}
