using Magic.Contexts.Rendering;
using Magic.Interfaces;
using Magic.Mathematics;
using Magic.Utils;
using System.Numerics;
using System.Runtime.InteropServices;

namespace DeferredRendererGem;

/// <summary>
/// The indirect draw list. A group is one draw-argument slot for one live (pipeline class, mesh) pair; a chunk is 64 groups
/// owned by one class, drawn with a single indirect call of 64 commands. Each group owns a region of the visible-id list
/// sized to the next power of two of its instance count; the template holds every slot's constant fields and its region
/// base, and each view's draw args start every frame as a copy of it so the culler only has to count. Nothing is ever
/// renumbered: a class that grows gets another chunk. Beside the template, one <see cref="GpuLodRow"/> per group says
/// which groups draw the lesser versions of its mesh, for the culler to pick from.
/// </summary>
internal sealed class Buckets
{
    public const int GroupsPerChunk = 64;

    public const uint ChunkBytes = GroupsPerChunk * DrawArgs.Size;

    private readonly RefCountTable<(PipelineClass Class, uint Mesh), Group> _groups = new();
    private readonly List<Stack<int>> _freeInChunk = [];
    private readonly List<int> _impostorChunks = [];
    private readonly List<int> _otherChunks = [];
    private readonly RangeAllocator _visibleSpace;
    private readonly uint _visibleCapacity;
    private DrawArgs[] _template = new DrawArgs[GroupsPerChunk];
    private GpuLodRow[] _lods = new GpuLodRow[GroupsPerChunk];
    private GpuGiGroup[] _giRows = new GpuGiGroup[GroupsPerChunk];

    public Buckets(IRendering gpu, uint visibleCapacity)
    {
        _visibleSpace = new RangeAllocator(visibleCapacity, 64 * 1024);
        _visibleCapacity = visibleCapacity;
        Template = new GrowableBuffer(gpu, GpuBufferUsage.GraphicsRead | GpuBufferUsage.ComputeRead, ChunkBytes);
        Lods = new GrowableBuffer(gpu, GpuBufferUsage.ComputeRead, GroupsPerChunk * GpuLodRow.Size);
        GiGroups = new GrowableBuffer(gpu, GpuBufferUsage.ComputeRead, GroupsPerChunk * GpuGiGroup.Size);
    }

    /// <summary>
    /// The CPU-written template each view's draw args start the frame as; the late pass seeds its own from this buffer.
    /// </summary>
    public GrowableBuffer Template { get; }

    public ReadOnlySpan<DrawArgs> TemplateRows => _template;

    /// <summary>
    /// One row per group, as the culler reads them.
    /// </summary>
    public GrowableBuffer Lods { get; }

    public ReadOnlySpan<GpuLodRow> LodRows => _lods;

    /// <summary>
    /// One row per group for the GI: the mesh's box and brick.
    /// </summary>
    public GrowableBuffer GiGroups { get; }

    public ReadOnlySpan<GpuGiGroup> GiRows => _giRows;

    public bool Dirty { get; set; } = true;

    /// <summary>
    /// One past the highest visible-id entry any group's region reaches: what a view's visible-id list must hold.
    /// </summary>
    public uint VisibleHighWater { get; private set; } = 4;

    public int ChunkCount => _freeInChunk.Count;

    /// <summary>
    /// The chunks of every class, for the draw loop: one indirect call per chunk.
    /// </summary>
    public Dictionary<PipelineClass, List<int>> ByClass { get; } = [];

    /// <summary>
    /// The chunks of every impostor class, or of every other class, ascending: what a depth draw of the impostors' cards,
    /// or of everything else, walks. Chunks are only ever added, each one past the last.
    /// </summary>
    public ReadOnlySpan<int> ChunksOf(bool impostors) => CollectionsMarshal.AsSpan(impostors ? _impostorChunks : _otherChunks);

    /// <summary>
    /// True when every group of the chunk is free: its indirect call would draw nothing.
    /// </summary>
    public bool IsEmpty(int chunk) => _freeInChunk[chunk].Count == GroupsPerChunk;

    /// <summary>
    /// The group index, saying whether the group was made now (its GI row is then still to be set).
    /// </summary>
    public uint Acquire(PipelineClass cls, uint meshSlot, (uint FirstIndex, uint IndexCount, int VertexOffset) range, out bool created)
    {
        created = !_groups.TryAcquire((cls, meshSlot), out Group group);
        if (created)
        {
            group = new Group(AllocateGroup(cls), RangeAllocator.Allocation.None, 0, range.FirstIndex, range.IndexCount, range.VertexOffset);
            _groups.Add((cls, meshSlot), group);
        }

        int refs = _groups.RefsOf((cls, meshSlot));
        if (refs <= group.Capacity)
            return group.Index;

        // Outgrown its region: a new one at the next power of two.
        uint capacity = Math.Max(4u, BitOperations.RoundUpToPowerOf2((uint)refs));
        _visibleSpace.Free(group.Region);
        RangeAllocator.Allocation region = _visibleSpace.Allocate(capacity);
        if (region.IsNone)
        {
            Debugging.Log.Error($"The visible-id list ({_visibleCapacity} entries) is full; a group of {refs} instances will not be drawn.");
            capacity = 0;
        }
        else
            VisibleHighWater = Math.Max(VisibleHighWater, region.Offset + capacity);

        group = group with { Region = region, Capacity = capacity };
        _groups.Set((cls, meshSlot), group);
        WriteTemplate(group);
        return group.Index;
    }

    /// <summary>
    /// Says which groups draw the lesser versions of <paramref name="group"/>'s mesh and under what height on screen,
    /// the highest threshold first; more than <see cref="GpuLodRow.Capacity"/> are cut. The row goes when the group does.
    /// </summary>
    public void SetLods(uint group, ReadOnlySpan<(float Threshold, uint Group)> lods)
    {
        Span<float> thresholds = stackalloc float[GpuLodRow.Capacity];
        Span<uint> groups = stackalloc uint[GpuLodRow.Capacity];
        for (int i = 0; i < Math.Min(lods.Length, GpuLodRow.Capacity); i++)
            (thresholds[i], groups[i]) = lods[i];

        GpuLodRow row = new()
        {
            Thresholds = new Vector4(thresholds[0], thresholds[1], thresholds[2], thresholds[3]),
            Groups = new UintVector4(groups[0], groups[1], groups[2], groups[3]),
        };

        ref GpuLodRow current = ref _lods[group];
        if (current.Thresholds == row.Thresholds && current.Groups == row.Groups)
            return;

        current = row;
        Dirty = true;
    }

    /// <summary>
    /// Sets the GI row of a group: its mesh's box in the mesh's own space and the mesh's occupancy brick.
    /// </summary>
    public void SetGi(uint group, in Aabb box, uint brick, uint meshSlot)
    {
        _giRows[group] = new GpuGiGroup { BoxMin = new Vector4(box.Min, 0f), BoxMax = new Vector4(box.Max, 0f), Brick = brick, MeshSlot = meshSlot };
        Dirty = true;
    }

    /// <summary>
    /// Drops a reference; true when it was the last and the group is gone.
    /// </summary>
    public bool Release(PipelineClass cls, uint meshSlot)
    {
        if (!_groups.Release((cls, meshSlot), out Group group))
            return false;

        _lods[group.Index] = default;
        _giRows[group.Index] = default;
        _visibleSpace.Free(group.Region);
        WriteTemplate(group with { Region = RangeAllocator.Allocation.None, Capacity = 0 });
        _freeInChunk[(int)group.Index / GroupsPerChunk].Push((int)group.Index % GroupsPerChunk);
        return true;
    }

    private uint AllocateGroup(PipelineClass cls)
    {
        if (!ByClass.TryGetValue(cls, out List<int>? chunks))
            ByClass[cls] = chunks = [];

        foreach (int chunk in chunks)
        {
            if (_freeInChunk[chunk].Count > 0)
                return (uint)((chunk * GroupsPerChunk) + _freeInChunk[chunk].Pop());
        }

        int created = _freeInChunk.Count;
        Stack<int> free = new();
        for (int i = GroupsPerChunk - 1; i >= 1; i--)
            free.Push(i);
        _freeInChunk.Add(free);
        chunks.Add(created);
        (cls.Variant.HasFlag(SurfaceVariant.Impostor) ? _impostorChunks : _otherChunks).Add(created);

        if (_template.Length < _freeInChunk.Count * GroupsPerChunk)
        {
            Array.Resize(ref _template, _freeInChunk.Count * GroupsPerChunk);
            Array.Resize(ref _lods, _freeInChunk.Count * GroupsPerChunk);
            Array.Resize(ref _giRows, _freeInChunk.Count * GroupsPerChunk);
        }

        Dirty = true;

        return (uint)(created * GroupsPerChunk);
    }

    private void WriteTemplate(in Group group)
    {
        _template[group.Index] = new DrawArgs
        {
            IndexCount = group.Capacity > 0 ? group.IndexCount : 0,
            InstanceCount = 0,
            FirstIndex = group.FirstIndex,
            VertexOffset = group.VertexOffset,
            FirstInstance = group.Region.IsNone ? 0 : group.Region.Offset,
        };
        Dirty = true;
    }

    /// <summary>
    /// One draw-argument slot: its index, its region of the visible-id list and capacity, and the mesh range it draws.
    /// </summary>
    private readonly record struct Group(uint Index, RangeAllocator.Allocation Region, uint Capacity, uint FirstIndex, uint IndexCount, int VertexOffset);
}
