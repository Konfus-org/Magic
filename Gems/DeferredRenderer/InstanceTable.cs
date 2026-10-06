using Magic.Contexts;
using Magic.Contexts.Rendering;
using Magic.Contexts.Assets;
using Magic.Interfaces;
using Magic.Mathematics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace DeferredRendererGem;

/// <summary>
/// Every drawn instance: its culling row and its transform rows, mirrored on the CPU so a move can compare and only changed
/// rows go up, and beside them what it was registered with (its model and material, and the next part of the same entity:
/// an entity is the chain that starts at its first part's slot). Slots come from a flat grid of 64 m cells, each owning
/// pages of 256 consecutive slots and a loose box that grows to enclose everything put in it: the GPU culls cells, then
/// only visits the pages of visible cells. Anything that can move, or is bigger than a cell, lives in cell 0, which is
/// always visible. Slot / 256 is the page, so nothing else has to be looked up to free a slot. A removed slot keeps its
/// row with the alive flag cleared until the page reuses it. The cells and pages are kept as the rows the GPU reads. The
/// dirty flags say which pages, and whether the cell and page tables, have to go up again.
/// </summary>
internal sealed class InstanceTable
{
    public const float CellSize = 64f;

    public const int PageSize = 256;

    /// <summary>
    /// What <see cref="NextPart"/> answers for an entity's last part.
    /// </summary>
    public const uint End = uint.MaxValue;

    private readonly Dictionary<long, int> _cellsByKey = [];
    private readonly List<GpuCell> _cells = [];
    private readonly List<Stack<int>> _cellRoom = []; // per cell: its pages with a free slot, so allocating never scans full ones
    private readonly List<GpuPage> _pages = [];
    private readonly List<Stack<uint>> _pageFree = []; // per page: the slots given back
    private GpuInstance[] _cull;
    private GpuInstanceXform[] _xform;
    private BoundingSphere[] _bounds;
    private float[] _cullRadii; // as registered: 0 = that of the bounds
    private Vector3[] _origins; // the model's point that sits at the entity's position
    private uint[] _meshSlots;
    private PipelineClass[] _classes;
    private Handle<Material>[] _materials;
    private ulong[] _models;
    private uint[] _next;
    private bool[] _dirtyPages = new bool[16];

    public InstanceTable(IRendering gpu, uint capacity)
    {
        _cull = new GpuInstance[capacity];
        _xform = new GpuInstanceXform[capacity];
        _bounds = new BoundingSphere[capacity];
        _cullRadii = new float[capacity];
        _origins = new Vector3[capacity];
        _meshSlots = new uint[capacity];
        _classes = new PipelineClass[capacity];
        _materials = new Handle<Material>[capacity];
        _models = new ulong[capacity];
        _next = new uint[capacity];

        const GpuBufferUsage read = GpuBufferUsage.GraphicsRead | GpuBufferUsage.ComputeRead;
        CullBuffer = new GrowableBuffer(gpu, read, capacity * GpuInstance.Size);
        XformBuffer = new GrowableBuffer(gpu, read, capacity * GpuInstanceXform.Size);
        CellBuffer = new GrowableBuffer(gpu, GpuBufferUsage.ComputeRead, 256 * GpuCell.Size);
        PageBuffer = new GrowableBuffer(gpu, GpuBufferUsage.ComputeRead, 256 * GpuPage.Size);

        // The dynamic cell.
        _cells.Add(new GpuCell { AabbMin = new Vector4(new Vector3(-1e9f), 0f), AabbMax = new Vector4(new Vector3(1e9f), 0f) });
        _cellRoom.Add([]);
    }

    public GrowableBuffer CullBuffer { get; }

    public GrowableBuffer XformBuffer { get; }

    public GrowableBuffer CellBuffer { get; }

    public GrowableBuffer PageBuffer { get; }

    /// <summary>
    /// Slots ever handed out; rows below it may be dead (alive flag clear).
    /// </summary>
    public uint HighWater { get; private set; }

    public uint Alive { get; private set; }

    public int CellCount => _cells.Count;

    public int PageCount => _pages.Count;

    public ReadOnlySpan<GpuInstance> Rows => _cull.AsSpan(0, (int)HighWater);

    public ReadOnlySpan<GpuInstanceXform> Xforms => _xform.AsSpan(0, (int)HighWater);

    /// <summary>
    /// The cell table as the GPU reads it.
    /// </summary>
    public ReadOnlySpan<GpuCell> Cells => CollectionsMarshal.AsSpan(_cells);

    /// <summary>
    /// The page table as the GPU reads it.
    /// </summary>
    public ReadOnlySpan<GpuPage> Pages => CollectionsMarshal.AsSpan(_pages);

    /// <summary>
    /// One flag per page: a row of it changed since the last upload.
    /// </summary>
    public Span<bool> DirtyPages => _dirtyPages.AsSpan(0, _pages.Count);

    public bool AnyDirty { get; set; }

    /// <summary>
    /// The slots drawn as a failure: with a material that failed, or as the failure cube because their model did not
    /// load. Kept by <c>Instancing</c>, which decides what an instance is drawn with.
    /// </summary>
    public HashSet<uint> Failed { get; } = [];

    public bool CellsDirty { get; set; } = true;

    public bool PagesDirty { get; set; } = true;

    /// <summary>
    /// A slot for one drawn mesh of <paramref name="model"/> with the material <paramref name="source"/> packed as
    /// <paramref name="material"/>. <paramref name="cullRadius"/> is the world-space radius it is size-culled by; 0 is
    /// that of its bounds.
    /// </summary>
    public uint Add(
        ulong model,
        uint meshSlot,
        in BoundingSphere meshBounds,
        float cullRadius,
        Handle<Material> source,
        MaterialSlot material,
        InstanceFlags flags,
        in Matrix4x4 world,
        Vector3 origin,
        uint bucketGroup,
        bool isStatic)
    {
        Matrix4x4 placed = Placed(world, origin);
        BoundingSphere sphere = meshBounds.Transform(placed);
        uint slot = Allocate(sphere, isStatic);
        while (slot >= _cull.Length)
            Grow();

        HighWater = Math.Max(HighWater, slot + 1);
        _bounds[slot] = meshBounds;
        _cullRadii[slot] = cullRadius;
        _origins[slot] = origin;
        _meshSlots[slot] = meshSlot;
        _classes[slot] = material.Class;
        _materials[slot] = source;
        _models[slot] = model;
        _next[slot] = End;
        _cull[slot] = new GpuInstance
        {
            MaterialSlot = material.Slot,
            BucketGroup = bucketGroup,
            Flags = InstanceFlags.Alive | flags,
        };
        Write(slot, placed, GpuInstanceXform.From(placed));
        Alive++;

        return slot;
    }

    /// <summary>
    /// <paramref name="next"/> is the part after <paramref name="slot"/> of the same entity.
    /// </summary>
    public void Link(uint slot, uint next)
    {
        _next[slot] = next;
    }

    /// <summary>
    /// A new world matrix; one equal to the stored rows changes nothing, so nothing goes up.
    /// </summary>
    public void Move(uint slot, in Matrix4x4 world)
    {
        if (!IsAlive(slot))
            return;

        Matrix4x4 placed = Placed(world, _origins[slot]);
        GpuInstanceXform rows = GpuInstanceXform.From(placed);
        if (rows.R0 == _xform[slot].R0 && rows.R1 == _xform[slot].R1 && rows.R2 == _xform[slot].R2)
            return;

        Write(slot, placed, rows);
    }

    /// <summary>
    /// Where an instance is drawn: its entity's world matrix, with the model moved so that its origin is at the entity's position.
    /// </summary>
    private static Matrix4x4 Placed(in Matrix4x4 world, Vector3 origin)
    {
        return origin == Vector3.Zero ? world : Matrix4x4.CreateTranslation(-origin) * world;
    }

    public void Remove(uint slot)
    {
        if (!IsAlive(slot))
            return;

        _cull[slot].Flags = InstanceFlags.None;
        Failed.Remove(slot);

        int pageIndex = (int)(slot / PageSize);
        Stack<uint> free = _pageFree[pageIndex];
        if (free.Count == 0 && _pages[pageIndex].Count == PageSize)
            _cellRoom[(int)_pages[pageIndex].Cell].Push(pageIndex); // it was full

        free.Push(slot % PageSize);
        Alive--;
        MarkDirty(slot);
    }

    /// <summary>
    /// A hidden instance keeps its slot and everything it holds; the culler passes over it.
    /// </summary>
    public void Hide(uint slot, bool hidden)
    {
        if (!IsAlive(slot) || ((_cull[slot].Flags & InstanceFlags.Hidden) != 0) == hidden)
            return;

        if (hidden)
            _cull[slot].Flags |= InstanceFlags.Hidden;
        else
            _cull[slot].Flags &= ~InstanceFlags.Hidden;

        MarkDirty(slot);
    }

    // Tested with a mask, not HasFlag: a Debug build boxes the enum for HasFlag, and this is asked per instance.
    public bool IsAlive(uint slot) => slot < HighWater && (_cull[slot].Flags & InstanceFlags.Alive) != 0;

    public PipelineClass ClassOf(uint slot) => _classes[slot];

    public uint BucketGroupOf(uint slot) => _cull[slot].BucketGroup;

    public uint MeshSlotOf(uint slot) => _meshSlots[slot];

    public Handle<Material> MaterialOf(uint slot) => _materials[slot];

    public ulong ModelOf(uint slot) => _models[slot];

    /// <summary>
    /// The next part of the entity <paramref name="slot"/> belongs to, or <see cref="End"/>.
    /// </summary>
    public uint NextPart(uint slot) => _next[slot];

    /// <summary>
    /// The instance's material changed class: take its class, slot and bucket again.
    /// </summary>
    public void Reclass(uint slot, MaterialSlot material, uint bucketGroup)
    {
        if (!IsAlive(slot))
            return;

        ref GpuInstance row = ref _cull[slot];
        if (_classes[slot] == material.Class && row.MaterialSlot == material.Slot && row.BucketGroup == bucketGroup)
            return;

        _classes[slot] = material.Class;
        row.MaterialSlot = material.Slot;
        row.BucketGroup = bucketGroup;
        MarkDirty(slot);
    }

    /// <summary>
    /// A slot for an instance with this world-space sphere; static ones land in the cell under their centre.
    /// </summary>
    private uint Allocate(in BoundingSphere sphere, bool isStatic)
    {
        int cellIndex = 0;
        if (isStatic && sphere.Radius <= CellSize)
        {
            long key = Key(sphere.Center);
            if (!_cellsByKey.TryGetValue(key, out cellIndex))
            {
                // Empty: the first box put in it is its bounds.
                cellIndex = _cells.Count;
                _cellsByKey[key] = cellIndex;
                _cells.Add(new GpuCell { AabbMin = new Vector4(new Vector3(float.MaxValue), 0f), AabbMax = new Vector4(new Vector3(float.MinValue), 0f) });
                _cellRoom.Add([]);
            }
        }

        if (cellIndex != 0)
        {
            ref GpuCell cell = ref CollectionsMarshal.AsSpan(_cells)[cellIndex];
            Vector3 radius = new(sphere.Radius);
            cell.AabbMin = Vector4.Min(cell.AabbMin, new Vector4(sphere.Center - radius, 0f));
            cell.AabbMax = Vector4.Max(cell.AabbMax, new Vector4(sphere.Center + radius, 0f));
            CellsDirty = true;
        }

        Stack<int> room = _cellRoom[cellIndex];
        if (room.Count == 0)
        {
            room.Push(_pages.Count);
            _pages.Add(new GpuPage { FirstInstance = (uint)_pages.Count * PageSize, Cell = (uint)cellIndex });
            _pageFree.Add([]);
            if (_dirtyPages.Length < _pages.Count)
                Array.Resize(ref _dirtyPages, _dirtyPages.Length * 2);
        }

        int pageIndex = room.Peek();
        ref GpuPage page = ref CollectionsMarshal.AsSpan(_pages)[pageIndex];
        Stack<uint> free = _pageFree[pageIndex];
        uint local;
        if (free.Count > 0)
            local = free.Pop();
        else
        {
            local = page.Count++;
            PagesDirty = true; // the page table carries the count
        }

        if (free.Count == 0 && page.Count == PageSize)
            room.Pop(); // full

        return ((uint)pageIndex * PageSize) + local;
    }

    private void Write(uint slot, in Matrix4x4 world, in GpuInstanceXform rows)
    {
        _xform[slot] = rows;
        BoundingSphere sphere = _bounds[slot].Transform(world);
        _cull[slot].Sphere = new Vector4(sphere.Center, sphere.Radius);
        _cull[slot].CullRadius = _cullRadii[slot] > 0f ? _cullRadii[slot] : sphere.Radius;

        // A negative determinant mirrors the winding; the shaders flip front-face and normals for it.
        float det = (world.M11 * ((world.M22 * world.M33) - (world.M23 * world.M32)))
            - (world.M12 * ((world.M21 * world.M33) - (world.M23 * world.M31)))
            + (world.M13 * ((world.M21 * world.M32) - (world.M22 * world.M31)));
        if (det < 0f)
            _cull[slot].Flags |= InstanceFlags.Mirrored;
        else
            _cull[slot].Flags &= ~InstanceFlags.Mirrored;

        MarkDirty(slot);
    }

    private void MarkDirty(uint slot)
    {
        _dirtyPages[slot / PageSize] = true;
        AnyDirty = true;
    }

    private void Grow()
    {
        int size = _cull.Length * 2;
        Array.Resize(ref _cull, size);
        Array.Resize(ref _xform, size);
        Array.Resize(ref _bounds, size);
        Array.Resize(ref _cullRadii, size);
        Array.Resize(ref _origins, size);
        Array.Resize(ref _meshSlots, size);
        Array.Resize(ref _classes, size);
        Array.Resize(ref _materials, size);
        Array.Resize(ref _models, size);
        Array.Resize(ref _next, size);
    }

    private static long Key(Vector3 position)
    {
        long x = (long)MathF.Floor(position.X / CellSize) & 0x1FFFFF;
        long y = (long)MathF.Floor(position.Y / CellSize) & 0x1FFFFF;
        long z = (long)MathF.Floor(position.Z / CellSize) & 0x1FFFFF;
        return (x << 42) | (y << 21) | z;
    }
}
