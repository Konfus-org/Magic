using System.Numerics;

namespace Magic.Contexts.Assets;

/// <summary>
/// One cluster of a <see cref="ClusterHierarchy"/>: a run of its vertices and triangles, all drawn with one material
/// slot, and what a renderer needs to cull it and to pick it.
/// </summary>
public readonly record struct Cluster
{
    public required int FirstVertex { get; init; }

    public required int VertexCount { get; init; }

    /// <summary>
    /// Its first triangle's first corner in <see cref="ClusterHierarchy.Indices"/>.
    /// </summary>
    public required int FirstIndex { get; init; }

    public required int TriangleCount { get; init; }

    public required int MaterialSlot { get; init; }

    /// <summary>
    /// 0 for the model's own triangles, one more for each time they were simplified.
    /// </summary>
    public required int Level { get; init; }

    /// <summary>
    /// The sphere around its triangles, centre and radius, for culling.
    /// </summary>
    public required Vector4 Bounds { get; init; }

    /// <summary>
    /// The sphere its error is measured from, centre and radius: its group's, around every cluster the group was made
    /// from, so it is never nearer the camera than what it stands for.
    /// </summary>
    public required Vector4 ErrorBounds { get; init; }

    /// <summary>
    /// Metres it may be from the model's own triangles: 0 for those, and never less than what it was simplified from.
    /// </summary>
    public required float Error { get; init; }

    /// <summary>
    /// <see cref="ErrorBounds"/> of the clusters it was simplified into; its own when it is of the coarsest.
    /// </summary>
    public required Vector4 ParentErrorBounds { get; init; }

    /// <summary>
    /// <see cref="Error"/> of the clusters it was simplified into: infinity when it is of the coarsest, so it is drawn
    /// however far away the model is.
    /// </summary>
    public required float ParentError { get; init; }
}

/// <summary>
/// A model's triangles in clusters of up to <see cref="MaxTriangles"/>, at every level at once, after Karis, "Nanite: A
/// Deep Dive" (2021). The finest clusters are the model's own triangles; then neighbouring clusters are grouped, each
/// group simplified to about half with its border kept where it is, and split into clusters again, level after level,
/// until a few are left. A renderer draws a cut through them: each cluster whose error is small enough from where it is
/// seen and whose parent's is not. So the parts of one model nearer the camera are finer than those farther away, and
/// as two clusters that meet were made either side of the same kept border, the cut has no cracks.
/// <para>
/// A cluster's <see cref="Cluster.Error"/> and <see cref="Cluster.ErrorBounds"/> are its group's, the same for every
/// cluster made from that group, and its parent's are those of the group it was simplified into, never smaller, so
/// every cluster decides alone and the clusters drawn still meet.
/// </para>
/// </summary>
public sealed class ClusterHierarchy
{
    /// <summary>
    /// The most triangles a cluster holds.
    /// </summary>
    public const int MaxTriangles = 128;

    /// <summary>
    /// The most vertices a cluster holds, so a triangle's corners are a byte each (<see cref="Indices"/>).
    /// </summary>
    public const int MaxVertices = 256;

    /// <summary>
    /// Every cluster's vertices, each cluster's a run of its own (<see cref="Cluster.FirstVertex"/>), in the model's
    /// space. Their positions lie on a grid <see cref="PositionStep"/> metres apart, the same for the whole model, so a
    /// vertex two clusters share is the same in both, and a renderer can pack them into integers without moving them.
    /// </summary>
    public required ReadOnlyMemory<Vertex> Vertices { get; init; }

    /// <summary>
    /// Three a triangle, each into its cluster's own vertices, 0 for its first.
    /// </summary>
    public required ReadOnlyMemory<byte> Indices { get; init; }

    /// <summary>
    /// Coarsest level last; within a level, in no order.
    /// </summary>
    public required ReadOnlyMemory<Cluster> Clusters { get; init; }

    /// <summary>
    /// Metres between the grid's points that every position lies on.
    /// </summary>
    public required float PositionStep { get; init; }

    /// <summary>
    /// The model as clusters <paramref name="error"/> metres or less from it everywhere: the clusters whose own
    /// error is that or less and whose parent's is more, merged into a mesh for each material slot they are drawn with.
    /// For what must be the model as it looks from a distance, without a view to project the error with, such as a
    /// distance field. Seen from <c>d</c> metres through a 60° view <c>h</c> pixels high, an error of
    /// <c>e</c> metres is about <c>e · h / (1.155 · d)</c> pixels.
    /// </summary>
    public IReadOnlyList<Mesh> Cut(float error)
    {
        SortedDictionary<int, (List<Vertex> Vertices, List<uint> Indices)> bySlot = [];
        ReadOnlySpan<Cluster> clusters = Clusters.Span;
        ReadOnlySpan<Vertex> vertices = Vertices.Span;
        ReadOnlySpan<byte> indices = Indices.Span;
        foreach (Cluster cluster in clusters)
        {
            if (cluster.Error > error || cluster.ParentError <= error) continue;

            if (!bySlot.TryGetValue(cluster.MaterialSlot, out (List<Vertex> Vertices, List<uint> Indices) into))
            {
                into = ([], []);
                bySlot[cluster.MaterialSlot] = into;
            }

            uint first = (uint)into.Vertices.Count;
            into.Vertices.AddRange(vertices.Slice(cluster.FirstVertex, cluster.VertexCount));
            foreach (byte index in indices.Slice(cluster.FirstIndex, cluster.TriangleCount * 3))
            {
                into.Indices.Add(first + index);
            }
        }

        List<Mesh> meshes = new(bySlot.Count);
        foreach ((int slot, (List<Vertex> Vertices, List<uint> Indices) mesh) in bySlot)
        {
            meshes.Add(
                new Mesh { Vertices = mesh.Vertices.ToArray(), Indices = mesh.Indices.ToArray(), MaterialSlot = slot });
        }

        return meshes;
    }
}
