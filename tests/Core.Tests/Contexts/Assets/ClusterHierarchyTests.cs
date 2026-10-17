using System.Numerics;
using Magic.Contexts.Assets;

namespace Magic.Tests.Contexts.Assets;

public sealed class ClusterHierarchyTests
{
    [Fact]
    public void Cut_takes_a_cluster_within_the_error_whose_parent_is_not()
    {
        ClusterHierarchy hierarchy = Triangles([ClusterOf(first: 0, slot: 0, error: 0, parentError: float.PositiveInfinity)]);

        IReadOnlyList<Mesh> meshes = hierarchy.Cut(0.1f);

        Assert.Equal(3, Assert.Single(meshes).Indices.Length);
    }

    [Fact]
    public void Cut_leaves_out_a_cluster_whose_parent_is_also_within_the_error()
    {
        ClusterHierarchy hierarchy = Triangles([ClusterOf(first: 0, slot: 0, error: 0, parentError: 0.05f)]);

        IReadOnlyList<Mesh> meshes = hierarchy.Cut(0.1f);

        Assert.Empty(meshes);
    }

    [Fact]
    public void Cut_leaves_out_a_cluster_past_the_error()
    {
        ClusterHierarchy hierarchy = Triangles([ClusterOf(first: 0, slot: 0, error: 1, parentError: float.PositiveInfinity)]);

        IReadOnlyList<Mesh> meshes = hierarchy.Cut(0.1f);

        Assert.Empty(meshes);
    }

    [Fact]
    public void Cut_merges_the_clusters_of_one_slot_into_one_mesh()
    {
        ClusterHierarchy hierarchy = Triangles(
        [
            ClusterOf(first: 0, slot: 0, error: 0, parentError: float.PositiveInfinity),
            ClusterOf(first: 1, slot: 0, error: 0, parentError: float.PositiveInfinity),
        ]);

        IReadOnlyList<Mesh> meshes = hierarchy.Cut(0.1f);

        Assert.Equal(6, Assert.Single(meshes).Indices.Length);
    }

    [Fact]
    public void Cut_rebases_each_clusters_indices_onto_the_merged_vertices()
    {
        ClusterHierarchy hierarchy = Triangles(
        [
            ClusterOf(first: 0, slot: 0, error: 0, parentError: float.PositiveInfinity),
            ClusterOf(first: 1, slot: 0, error: 0, parentError: float.PositiveInfinity),
        ]);

        IReadOnlyList<Mesh> meshes = hierarchy.Cut(0.1f);

        uint[] expected = [0, 1, 2, 3, 4, 5];
        Assert.Equal(expected, meshes[0].Indices.ToArray());
    }

    [Fact]
    public void Cut_keeps_each_clusters_own_vertices()
    {
        ClusterHierarchy hierarchy = Triangles(
        [
            ClusterOf(first: 0, slot: 0, error: 0, parentError: float.PositiveInfinity),
            ClusterOf(first: 1, slot: 0, error: 0, parentError: float.PositiveInfinity),
        ]);

        IReadOnlyList<Mesh> meshes = hierarchy.Cut(0.1f);

        Assert.Equal(hierarchy.Vertices.ToArray(), meshes[0].Vertices.ToArray());
    }

    [Fact]
    public void Cut_makes_a_mesh_for_each_material_slot()
    {
        ClusterHierarchy hierarchy = Triangles(
        [
            ClusterOf(first: 0, slot: 1, error: 0, parentError: float.PositiveInfinity),
            ClusterOf(first: 1, slot: 0, error: 0, parentError: float.PositiveInfinity),
        ]);

        IReadOnlyList<Mesh> meshes = hierarchy.Cut(0.1f);

        int[] expected = [0, 1];
        int[] slots = [meshes[0].MaterialSlot, meshes[1].MaterialSlot];
        Array.Sort(slots);
        Assert.Equal(expected, slots);
    }

    [Fact]
    public void Cut_of_no_clusters_is_empty()
    {
        ClusterHierarchy hierarchy = Triangles([]);

        IReadOnlyList<Mesh> meshes = hierarchy.Cut(0.1f);

        Assert.Empty(meshes);
    }

    /// <summary>
    /// A hierarchy of one-triangle clusters, each over its own three vertices: the first cluster the first three, the
    /// second the next three, with a run of vertices for every cluster named.
    /// </summary>
    private static ClusterHierarchy Triangles(Cluster[] clusters)
    {
        Vertex[] vertices = new Vertex[clusters.Length * 3];
        for (int i = 0; i < vertices.Length; i++)
        {
            vertices[i] = new Vertex { Position = new Vector3(i, 0, 0) };
        }

        byte[] indices = new byte[clusters.Length * 3];
        for (int i = 0; i < indices.Length; i++)
        {
            indices[i] = (byte)(i % 3);
        }

        return new ClusterHierarchy { Vertices = vertices, Indices = indices, Clusters = clusters, PositionStep = 0.001f };
    }

    /// <summary>
    /// The <paramref name="first"/>th one-triangle cluster of <see cref="Triangles"/>.
    /// </summary>
    private static Cluster ClusterOf(int first, int slot, float error, float parentError)
    {
        return new Cluster
        {
            FirstVertex = first * 3,
            VertexCount = 3,
            FirstIndex = first * 3,
            TriangleCount = 1,
            MaterialSlot = slot,
            Level = 0,
            Bounds = default,
            ErrorBounds = default,
            Error = error,
            ParentErrorBounds = default,
            ParentError = parentError,
        };
    }
}
