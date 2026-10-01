using Magic.Contexts.Assets;
using Magic.Interfaces;
using Magic.Mathematics;
using Magic.Utils;
using System.Numerics;

namespace Magic.Contexts.Rendering;

/// <summary>
/// Every mesh on the GPU: vertices and indices sub-allocated from one mega vertex buffer and one mega index buffer, so all
/// geometry is drawn from the same two bindings and the culler only has to write draw arguments. Models are reference
/// counted by id; one that could not be loaded keeps an entry too, on slot 0, a unit cube only ever used for that: the
/// instance is flagged and the failure shader paints it red. <see cref="Pending"/> is the geometry not uploaded yet.
/// </summary>
internal sealed class MeshTable
{
    /// <summary>What anything that cannot be drawn is: one part on slot 0 with material slot 0.</summary>
    public static readonly (uint MeshSlot, int MaterialSlot)[] Placeholder = [(0, 0)];

    private readonly OffsetAllocator _vertexSpace;
    private readonly OffsetAllocator _indexSpace;
    private readonly List<Placement?> _slots = [];
    private readonly Stack<uint> _free = [];
    private readonly RefCountTable<ulong, ModelEntry> _models = new();

    public MeshTable(IRendering gpu, uint vertexCapacity, uint indexCapacity)
    {
        VertexBuffer = gpu.CreateBuffer(GpuBufferUsage.Vertex, vertexCapacity * Vertex.Size);
        IndexBuffer = gpu.CreateBuffer(GpuBufferUsage.Index, indexCapacity * 4);
        _vertexSpace = new OffsetAllocator(vertexCapacity, 16 * 1024);
        _indexSpace = new OffsetAllocator(indexCapacity, 16 * 1024);

        Mesh cube = UnitCube();
        cube.ComputeBounds();
        Add(cube); // slot 0
    }

    public GpuBuffer VertexBuffer { get; }

    public GpuBuffer IndexBuffer { get; }

    public int Count => _slots.Count - _free.Count;

    /// <summary>Geometry added since the last upload: where it goes (in vertices and indices) and what it is.</summary>
    public List<(uint FirstVertex, uint FirstIndex, Vertex[] Vertices, uint[] Indices)> Pending { get; } = [];

    /// <summary>Takes another reference to a model already here; false when it is not (then <see cref="Add"/> it).</summary>
    public bool TryAcquire(ulong modelId, out (uint MeshSlot, int MaterialSlot)[] parts)
    {
        bool found = _models.TryAcquire(modelId, out ModelEntry entry);
        parts = entry.Parts;
        return found;
    }

    /// <summary>
    /// The model's meshes in slots and its parts on them, with one reference; a model that did not load (null) is the
    /// placeholder part alone.
    /// </summary>
    public (uint MeshSlot, int MaterialSlot)[] Add(ulong modelId, Model? model)
    {
        ModelEntry entry = BuildEntry(model);
        _models.Add(modelId, entry);
        return entry.Parts;
    }

    public void Release(ulong modelId)
    {
        if (!_models.Release(modelId, out ModelEntry entry))
            return;

        foreach (uint slot in entry.Slots)
            Remove(slot);
    }

    public bool Owns(ulong modelId)
    {
        return _models.Contains(modelId);
    }

    public BoundingSphere Bounds(uint slot)
    {
        return _slots[(int)slot]?.Bounds ?? default;
    }

    /// <summary>What a draw of the slot needs: index range and vertex base.</summary>
    public (uint FirstIndex, uint IndexCount, int VertexOffset) Range(uint slot)
    {
        Placement placement = _slots[(int)slot] ?? _slots[0]!;
        return (placement.Indices.Offset, placement.IndexCount, (int)placement.Vertices.Offset);
    }

    private ModelEntry BuildEntry(Model? model)
    {
        if (model is null)
            return new ModelEntry([], Placeholder); // the asset manager logged why

        uint[] slots = new uint[model.Meshes.Length];
        for (int i = 0; i < slots.Length; i++)
            slots[i] = Add(model.Meshes[i]);

        (uint, int)[] parts = new (uint, int)[model.Parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            uint meshSlot = slots.Length == 0 ? 0 : slots[Math.Clamp(model.Parts[i].MeshIndex, 0, slots.Length - 1)];
            parts[i] = (meshSlot, model.Parts[i].MaterialSlot);
        }

        return new ModelEntry(slots, parts.Length == 0 ? Placeholder : parts);
    }

    private uint Add(Mesh mesh)
    {
        if (mesh.Bounds.Radius == 0f && mesh.Vertices.Length > 0)
            mesh.ComputeBounds(); // a loader that did not

        OffsetAllocator.Allocation vertices = _vertexSpace.Allocate((uint)Math.Max(1, mesh.Vertices.Length));
        OffsetAllocator.Allocation indices = _indexSpace.Allocate((uint)Math.Max(1, mesh.Indices.Length));
        if (vertices.IsNone || indices.IsNone)
        {
            Debugging.Log.Error($"The mesh buffers are full ({_vertexSpace.FreeStorage} vertices, {_indexSpace.FreeStorage} indices free); the mesh draws as failed.");
            _vertexSpace.Free(vertices);
            _indexSpace.Free(indices);
            return 0;
        }

        Placement slot = new(vertices, indices, (uint)mesh.Indices.Length, mesh.Bounds);
        uint index;
        if (_free.Count > 0)
        {
            index = _free.Pop();
            _slots[(int)index] = slot;
        }
        else
        {
            index = (uint)_slots.Count;
            _slots.Add(slot);
        }

        Pending.Add((vertices.Offset, indices.Offset, mesh.Vertices, mesh.Indices));
        return index;
    }

    private void Remove(uint index)
    {
        if (index == 0 || _slots[(int)index] is not { } slot)
            return;

        _vertexSpace.Free(slot.Vertices);
        _indexSpace.Free(slot.Indices);
        _slots[(int)index] = null;
        _free.Push(index);
    }

    /// <summary>A 2 m cube, -1..1, clockwise from outside, one normal per face, tangents along the face's u, uvs per face.</summary>
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

    /// <summary>Where one mesh lives in the mega buffers, and its bounds.</summary>
    private sealed record Placement(OffsetAllocator.Allocation Vertices, OffsetAllocator.Allocation Indices, uint IndexCount, BoundingSphere Bounds);

    /// <summary>A loaded model: the mesh slots it owns and its parts on them.</summary>
    private readonly record struct ModelEntry(uint[] Slots, (uint MeshSlot, int MaterialSlot)[] Parts);
}
