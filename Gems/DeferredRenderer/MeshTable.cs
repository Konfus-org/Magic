using Magic.Contexts.Rendering;
using Magic.Contexts.Assets;
using Magic.Interfaces;
using Magic.Mathematics;
using Magic.Utils;
using System.Numerics;

namespace DeferredRendererGem;

/// <summary>
/// A loaded model: the mesh slots it owns, those of its lesser versions among them, its parts on its own, and its
/// <see cref="Model.Origin"/> as it was when it was placed.
/// </summary>
internal readonly record struct ModelEntry(uint[] Slots, (uint MeshSlot, int MaterialSlot)[] Parts, Vector3 Origin);

/// <summary>
/// Every model in use, id to <see cref="ModelEntry"/>, reference counted, and every mesh of them on the GPU: vertices and
/// indices sub-allocated from one mega vertex buffer and one mega index buffer, so all geometry is drawn from the same two
/// bindings and the culler only has to write draw arguments. A model that could not be loaded keeps an entry too, on
/// slot 0, a unit cube only ever used for that: the instance is flagged and the failure shader paints it red.
/// A mesh may have lesser versions (LODs), meshes of their own that it names with the height on screen under which each
/// is drawn in its place. <see cref="Pending"/> is the geometry not uploaded yet.
/// </summary>
internal sealed class MeshTable : RefCountTable<ulong, ModelEntry>
{
    /// <summary>
    /// What anything that cannot be drawn is: one part on slot 0 with material slot 0.
    /// </summary>
    public static readonly (uint MeshSlot, int MaterialSlot)[] Placeholder = [(0, 0)];

    private readonly RangeAllocator _vertexSpace;
    private readonly RangeAllocator _indexSpace;
    private readonly Slots<Placement> _placements = new();

    public MeshTable(IRendering gpu, uint vertexCapacity, uint indexCapacity)
    {
        VertexBuffer = gpu.CreateBuffer(GpuBufferUsage.Vertex | GpuBufferUsage.ComputeRead, vertexCapacity * Vertex.Size);
        IndexBuffer = gpu.CreateBuffer(GpuBufferUsage.Index | GpuBufferUsage.ComputeRead, indexCapacity * 4);
        _vertexSpace = new RangeAllocator(vertexCapacity, 16 * 1024);
        _indexSpace = new RangeAllocator(indexCapacity, 16 * 1024);

        Mesh cube = UnitCube();
        cube.ComputeBounds();
        Place(cube); // slot 0
    }

    public GpuBuffer VertexBuffer { get; }

    public GpuBuffer IndexBuffer { get; }

    /// <summary>
    /// The meshes placed, the failure cube among them.
    /// </summary>
    public int MeshCount => _placements.Used;

    /// <summary>
    /// Geometry placed since the last upload: where it goes (in vertices and indices) and what it is.
    /// </summary>
    public List<(uint FirstVertex, uint FirstIndex, Vertex[] Vertices, uint[] Indices)> Pending { get; } = [];

    public BoundingSphere Bounds(uint slot)
    {
        return _placements[slot]?.Bounds ?? default;
    }

    /// <summary>
    /// The mesh's box in its own space.
    /// </summary>
    public Aabb Box(uint slot)
    {
        return _placements[slot]?.Box ?? default;
    }

    /// <summary>
    /// The slot's lesser versions, the highest threshold first; none for most.
    /// </summary>
    public (float Threshold, uint MeshSlot)[] Lods(uint slot)
    {
        return _placements[slot]?.Lods ?? [];
    }

    public void SetLods(uint slot, (float Threshold, uint MeshSlot)[] lods)
    {
        if (_placements[slot] is { } placement)
            placement.Lods = lods;
    }

    /// <summary>
    /// What a draw of the slot needs: index range and vertex base.
    /// </summary>
    public (uint FirstIndex, uint IndexCount, int VertexOffset) Range(uint slot)
    {
        if ((_placements[slot] ?? _placements[0]) is not { } placement)
            return default;

        return (placement.Indices.Offset, placement.IndexCount, (int)placement.Vertices.Offset);
    }

    /// <summary>
    /// A slot for the mesh, its geometry queued for upload; slot 0, the failure cube, when the buffers are full (logged).
    /// </summary>
    public uint Place(Mesh mesh)
    {
        if (mesh.Bounds.Radius == 0f && mesh.Vertices.Length > 0)
            mesh.ComputeBounds(); // a loader that did not

        RangeAllocator.Allocation vertices = _vertexSpace.Allocate((uint)Math.Max(1, mesh.Vertices.Length));
        RangeAllocator.Allocation indices = _indexSpace.Allocate((uint)Math.Max(1, mesh.Indices.Length));
        if (vertices.IsNone || indices.IsNone)
        {
            Debugging.Log.Error($"The mesh buffers are full ({_vertexSpace.FreeStorage} vertices, {_indexSpace.FreeStorage} indices free); the mesh draws as failed.");
            _vertexSpace.Free(vertices);
            _indexSpace.Free(indices);
            return 0;
        }

        Pending.Add((vertices.Offset, indices.Offset, mesh.Vertices, mesh.Indices));
        return _placements.Add(new Placement(vertices, indices, (uint)mesh.Indices.Length, mesh.Bounds, mesh.Box));
    }

    /// <summary>
    /// Gives the slot's ranges back; the failure cube stays.
    /// </summary>
    public void Remove(uint slot)
    {
        if (slot == 0 || _placements[slot] is not { } placement)
            return;

        _vertexSpace.Free(placement.Vertices);
        _indexSpace.Free(placement.Indices);
        _placements.Remove(slot);
    }

    /// <summary>
    /// A 2 m cube, -1..1, clockwise from outside, one normal per face, tangents along the face's u, uvs per face.
    /// </summary>
    private static Mesh UnitCube()
    {
        (Vector3 N, Vector3 U, Vector3 V)[] faces =
        [
            (Vector3.UnitX, -Vector3.UnitZ, Vector3.UnitY), (-Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY),
            (Vector3.UnitY, Vector3.UnitX, -Vector3.UnitZ), (-Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ),
            (Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY), (-Vector3.UnitZ, -Vector3.UnitX, Vector3.UnitY),
        ];
        Vertex[] vertices = new Vertex[24];
        uint[] indices = new uint[36];
        for (int face = 0; face < 6; face++)
        {
            (Vector3 n, Vector3 u, Vector3 v) = faces[face];
            Vector3[] corners = [n - u - v, n - u + v, n + u + v, n + u - v];
            Vector2[] uvs = [new(0, 1), new(0, 0), new(1, 0), new(1, 1)];
            for (int corner = 0; corner < 4; corner++)
                vertices[(face * 4) + corner] = new Vertex { Position = corners[corner], Normal = n, Tangent = new Vector4(u, 1f), Uv = uvs[corner] };

            // cross(first - a, c - a) must point along n: the engine's clockwise-from-outside rule.
            uint first = (uint)(face * 4);
            Vector3 cross = Vector3.Cross(corners[1] - corners[0], corners[2] - corners[0]);
            bool flip = Vector3.Dot(cross, n) < 0;
            uint[] order = flip ? [first, first + 2, first + 1, first, first + 3, first + 2] : [first, first + 1, first + 2, first, first + 2, first + 3];
            order.CopyTo(indices, face * 6);
        }

        return new Mesh { Vertices = vertices, Indices = indices };
    }

    /// <summary>
    /// Where one mesh lives in the mega buffers, and its bounds.
    /// </summary>
    private sealed record Placement(RangeAllocator.Allocation Vertices, RangeAllocator.Allocation Indices, uint IndexCount, BoundingSphere Bounds, Aabb Box)
    {
        public (float Threshold, uint MeshSlot)[] Lods { get; set; } = [];
    }
}
