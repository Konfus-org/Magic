using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Files;
using Magic.Files.Types;
using Magic.Gpu;
using Magic.Rendering;
using Magic.Utils;

namespace Magic.GIRendering;

/// <summary>
/// The instance table: a slot per renderer, kept from frame to frame, built from a snapshot of the renderers. A frame's
/// work is what changed since the last snapshot: a block of renderers that is the same byte for byte costs a compare,
/// a renderer that changed or arrived is built again, and one that left frees its slot. Rows that changed are uploaded
/// in runs; a still scene uploads nothing.
/// </summary>
internal sealed class InstanceTable(IGraphicsApi gpu, RenderGraph graph) : IDisposable
{
    public const int Capacity = 1 << 18;

    /// <summary>
    /// How many renderers are compared at once: a still block is a few vector compares.
    /// </summary>
    private const int BlockSize = 64;

    private readonly GpuInstance[] _rows = new GpuInstance[Capacity];

    private readonly InstanceExtra[] _extras = new InstanceExtra[Capacity];

    private readonly Entity[] _entityOf = new Entity[Capacity];

    /// <summary>
    /// Each slot's index in the snapshot it was last read from.
    /// </summary>
    private readonly int[] _indexOf = new int[Capacity];

    private readonly bool[] _isUsed = new bool[Capacity];

    private readonly bool[] _isDirty = new bool[Capacity];

    /// <summary>
    /// Whether the slot's model is still being made, so its row draws nothing yet.
    /// </summary>
    private readonly bool[] _isWaiting = new bool[Capacity];

    /// <summary>
    /// The fill each slot was last set in.
    /// </summary>
    private readonly uint[] _seen = new uint[Capacity];

    private readonly Dictionary<Entity, int> _slotOf = [];

    private readonly Stack<int> _free = new();

    private readonly List<int> _dirtySlots = [];

    /// <summary>
    /// The slots waiting for their model, tried again when a model is made or fails.
    /// </summary>
    private readonly List<int> _waiting = [];

    /// <summary>
    /// The slots whose world changed in the last fill, whose previous world catches up in the next.
    /// </summary>
    private readonly List<int> _moved = [];

    /// <summary>
    /// The slots whose renderer is no longer at its index in this fill: freed unless it was set at another.
    /// </summary>
    private readonly List<int> _left = [];

    /// <summary>
    /// The slot of each index of <see cref="Renderers"/>, -1 where the table was full.
    /// </summary>
    private int[] _slotAt = new int[Capacity];

    /// <summary>
    /// <see cref="_slotAt"/> of the last snapshot.
    /// </summary>
    private int[] _lastSlotAt = new int[Capacity];

    /// <summary>
    /// The snapshot the last fill read.
    /// </summary>
    private EcsSnapshot<Transform, Renderer> _last = new();

    private uint _fill;

    private int _settledModels = -1;

    /// <summary>
    /// How many renderers of this fill and of the last found the table full: while the last left any, no block is
    /// still, so they are tried again.
    /// </summary>
    private int _unslotted;

    private int _lastUnslotted;

    private bool _isFullLogged;

    public GpuBuffer Buffer { get; private set; }

    /// <summary>
    /// One past the last slot in use: how many rows the cull walks.
    /// </summary>
    public int Count { get; private set; }

    /// <summary>
    /// Whether any row differs from what <see cref="Upload"/> last copied.
    /// </summary>
    public bool HasChanges => _dirtySlots.Count > 0;

    /// <summary>
    /// The renderers the next <see cref="Fill"/> reads, for the ECS to snapshot into.
    /// </summary>
    public EcsSnapshot<Transform, Renderer> Renderers { get; private set; } = new();

    /// <summary>
    /// The rows as the GPU reads them, by instance index; a freed slot's row draws nothing.
    /// </summary>
    public ReadOnlySpan<GpuInstance> Rows => _rows.AsSpan(0, Count);

    /// <summary>
    /// What the CPU keeps beside each row, by instance index.
    /// </summary>
    public ReadOnlySpan<InstanceExtra> Extras => _extras.AsSpan(0, Count);

    public void Dispose()
    {
        if (!Buffer.IsNone)
            gpu.Destroy(Buffer);
    }

    public Result Create()
    {
        Result<GpuBuffer> buffer = gpu.CreateBuffer(
            new BufferDescription
            {
                Bytes = (long)Capacity * GpuInstance.Bytes,
                Usage = BufferUsages.Storage,
                Name = "Instance table",
            }
        );
        if (!buffer.Ok)
            return Result.Failure($"the instance table was not made: {buffer.Error}");

        Buffer = buffer.Value;

        return Result.Success();
    }

    /// <summary>
    /// The rows of <see cref="Renderers"/>, from what changed since the last fill: its model's place in the pools and
    /// its material rows, or an empty row while its model is being made. How many rows the cull walks.
    /// </summary>
    public int Fill(GeometryPools pools, MaterialTable materials, Handle<Material> failureMaterial)
    {
        _fill++;
        RowInputs inputs = new(pools, materials, failureMaterial);
        CatchUpMoved();
        ReadChanged(inputs);
        FreeLeft();
        if (pools.SettledCount != _settledModels)
        {
            _settledModels = pools.SettledCount;
            RetryWaiting(inputs);
        }

        (Renderers, _last) = (_last, Renderers);
        (_slotAt, _lastSlotAt) = (_lastSlotAt, _slotAt);
        (_lastUnslotted, _unslotted) = (_unslotted, 0);

        return Count;
    }

    /// <summary>
    /// Copies the rows that changed into the table, each run of them in one copy, in the frame's upload pass.
    /// </summary>
    public void Upload()
    {
        if (_dirtySlots.Count == 0)
            return;

        _dirtySlots.Sort();
        GpuUpload upload = gpu.BeginUpload(_dirtySlots.Count * GpuInstance.Bytes);
        Span<GpuInstance> staged = MemoryMarshal.Cast<byte, GpuInstance>(upload.Bytes);
        graph.Use(Buffer, GpuAccess.CopyDestination);
        int runStart = 0;
        for (int i = 0; i < _dirtySlots.Count; i++)
        {
            staged[i] = _rows[_dirtySlots[i]];
            _isDirty[_dirtySlots[i]] = false;
            bool endsRun = i + 1 == _dirtySlots.Count || _dirtySlots[i + 1] != _dirtySlots[i] + 1;
            if (!endsRun)
                continue;

            int rows = i + 1 - runStart;
            graph.Recorder.CopyBuffer(
                upload.Buffer,
                upload.Offset + ((ulong)runStart * GpuInstance.Bytes),
                Buffer,
                (ulong)_dirtySlots[runStart] * GpuInstance.Bytes,
                (ulong)rows * GpuInstance.Bytes
            );
            runStart = i + 1;
        }

        _dirtySlots.Clear();
    }

    private static bool IsSame<T>(ReadOnlySpan<T> now, ReadOnlySpan<T> last)
        where T : unmanaged => MemoryMarshal.AsBytes(now).SequenceEqual(MemoryMarshal.AsBytes(last));

    /// <summary>
    /// A renderer's row, with its model resident: where it is, its bounds in the world and its material rows.
    /// </summary>
    private static GpuInstance CreateRow(
        Transform transform,
        Renderer renderer,
        ResidentModel resident,
        in RowInputs inputs
    )
    {
        Span<uint> materialRows = stackalloc uint[MaterialSlots.Capacity];
        ReadOnlySpan<Handle<Material>> slotHandles = renderer.Materials;
        for (int slot = 0; slot < MaterialSlots.Capacity; slot++)
        {
            materialRows[slot] = inputs.Materials.GetRow(
                resident.IsFailure ? inputs.FailureMaterial : slotHandles[slot]
            );
        }

        Matrix4x4 worldMatrix = transform.Matrix;
        Matrix4x4.Invert(worldMatrix, out Matrix4x4 inverseWorld);
        Vector3 scale = transform.Scale;
        float maxScale = Math.Max(Math.Abs(scale.X), Math.Max(Math.Abs(scale.Y), Math.Abs(scale.Z)));

        return new GpuInstance
        {
            World = worldMatrix,
            InverseWorld = inverseWorld,
            BoundsCenter = Vector3.Transform(resident.BoundsCenter, worldMatrix),
            BoundsRadius = resident.BoundsRadius * maxScale,
            ClusterOffset = resident.ClusterOffset,
            ClusterCount = resident.ClusterCount,
            Material0 = materialRows[0],
            Material1 = materialRows[1],
            Material2 = materialRows[2],
            Material3 = materialRows[3],
            Material4 = materialRows[4],
            Material5 = materialRows[5],
            Material6 = materialRows[6],
            Material7 = materialRows[7],
        };
    }

    /// <summary>
    /// The previous world of each row that moved in the last fill becomes its world, so a thing that stopped stops
    /// moving on screen.
    /// </summary>
    private void CatchUpMoved()
    {
        foreach (int slot in _moved)
        {
            if (!_isUsed[slot] || _rows[slot].PreviousWorld == _rows[slot].World)
                continue;

            _rows[slot].PreviousWorld = _rows[slot].World;
            MarkDirty(slot);
        }

        _moved.Clear();
    }

    /// <summary>
    /// Every block of renderers: one the last snapshot had byte for byte at the same indexes keeps its slots, one it
    /// had at other indexes, as an entity made or destroyed earlier in the snapshot shifts the rest, keeps them at
    /// their new indexes, and any other is read renderer by renderer. The renderers the last snapshot had at the
    /// indexes of a block that moved or changed, and past the end of this snapshot, have left their index.
    /// </summary>
    private void ReadChanged(in RowInputs inputs)
    {
        int count = Renderers.Count;
        for (int start = 0; start < count; start += BlockSize)
        {
            int length = Math.Min(BlockSize, count - start);
            if (IsSameAs(start, start, length))
            {
                _lastSlotAt.AsSpan(start, length).CopyTo(_slotAt.AsSpan(start));
                continue;
            }

            if (TryGetLastStart(start, length, out int lastStart))
            {
                KeepShifted(start, lastStart, length);
                continue;
            }

            for (int index = start; index < start + length; index++)
            {
                ReadRenderer(index, inputs);
            }
        }

        for (int index = count; index < _last.Count; index++)
        {
            _left.Add(_lastSlotAt[index]);
        }
    }

    /// <summary>
    /// Whether the block of renderers at <paramref name="start"/> is byte for byte what the last snapshot had from
    /// <paramref name="lastStart"/>, every one with a slot.
    /// </summary>
    private bool IsSameAs(int start, int lastStart, int length) =>
        _lastUnslotted == 0
        && lastStart + length <= _last.Count
        && IsSame(Renderers.Entities.Slice(start, length), _last.Entities.Slice(lastStart, length))
        && IsSame(Renderers.First.Slice(start, length), _last.First.Slice(lastStart, length))
        && IsSame(Renderers.Second.Slice(start, length), _last.Second.Slice(lastStart, length));

    /// <summary>
    /// Where the last snapshot had the block of renderers at <paramref name="start"/> whole, when only its index moved:
    /// found from the index its first renderer's slot was read from.
    /// </summary>
    private bool TryGetLastStart(int start, int length, out int lastStart)
    {
        lastStart = 0;
        if (!_slotOf.TryGetValue(Renderers.Entities[start], out int slot))
            return false;

        lastStart = _indexOf[slot];

        return lastStart != start && IsSameAs(start, lastStart, length);
    }

    /// <summary>
    /// The slots of a block that only moved, from <paramref name="lastStart"/> in the last snapshot to
    /// <paramref name="start"/>; the renderers the last snapshot had at its new indexes have left them.
    /// </summary>
    private void KeepShifted(int start, int lastStart, int length)
    {
        for (int i = 0; i < length; i++)
        {
            int slot = _lastSlotAt[lastStart + i];
            _slotAt[start + i] = slot;
            _indexOf[slot] = start + i;
            _seen[slot] = _fill;
            if (start + i < _last.Count)
                _left.Add(_lastSlotAt[start + i]);
        }
    }

    /// <summary>
    /// The renderer at <paramref name="index"/>: its slot kept when it is byte for byte what the last snapshot had for
    /// it, here or at the index it moved from, else its row set again. The renderer the last snapshot had here, when
    /// another, has left this index.
    /// </summary>
    private void ReadRenderer(int index, in RowInputs inputs)
    {
        Entity entity = Renderers.Entities[index];
        if (index < _last.Count && _last.Entities[index] != entity)
            _left.Add(_lastSlotAt[index]);

        if (_slotOf.TryGetValue(entity, out int kept) && IsUnchanged(index, _indexOf[kept]))
        {
            _slotAt[index] = kept;
            _indexOf[kept] = index;
            _seen[kept] = _fill;
            return;
        }

        int slot = Set(entity, index, inputs);
        _slotAt[index] = slot;
        if (slot < 0)
            _unslotted++;
    }

    /// <summary>
    /// Whether the renderer at <paramref name="index"/> is the same entity, byte for byte, as the last snapshot had at
    /// <paramref name="lastIndex"/>.
    /// </summary>
    private bool IsUnchanged(int index, int lastIndex) =>
        lastIndex < _last.Count
        && _last.Entities[lastIndex] == Renderers.Entities[index]
        && IsSame(Renderers.First.Slice(index, 1), _last.First.Slice(lastIndex, 1))
        && IsSame(Renderers.Second.Slice(index, 1), _last.Second.Slice(lastIndex, 1));

    /// <summary>
    /// Frees the slots of the renderers that left their index and were not set at another this fill; their rows draw
    /// nothing from now on.
    /// </summary>
    private void FreeLeft()
    {
        foreach (int slot in _left)
        {
            if (slot < 0 || !_isUsed[slot] || _seen[slot] == _fill)
                continue;

            _slotOf.Remove(_entityOf[slot]);
            _isUsed[slot] = false;
            _isWaiting[slot] = false;
            _free.Push(slot);
            _rows[slot] = default;
            _extras[slot] = default;
            MarkDirty(slot);
        }

        _left.Clear();
    }

    /// <summary>
    /// Sets the rows still waiting for their model again, once a model was made or failed since the last try.
    /// </summary>
    private void RetryWaiting(in RowInputs inputs)
    {
        int count = _waiting.Count;
        for (int i = 0; i < count; i++)
        {
            int slot = _waiting[i];
            if (!_isUsed[slot] || !_isWaiting[slot])
                continue;

            _isWaiting[slot] = false;
            Set(_entityOf[slot], _indexOf[slot], inputs);
        }

        _waiting.RemoveRange(0, count);
    }

    /// <summary>
    /// The row of the renderer at <paramref name="index"/> of <see cref="Renderers"/>, uploaded when it differs from
    /// the last; its previous world is what its row held, so a thing that moved carries where it was. The slot it is
    /// in, or -1 when the table is full.
    /// </summary>
    private int Set(Entity entity, int index, in RowInputs inputs)
    {
        if (!TryGetSlot(entity, out int slot, out bool isNew))
            return -1;

        Renderer renderer = Renderers.Second[index];
        ResidentModel? resident = renderer.Model == Handle<Model>.None ? null : inputs.Pools.Get(renderer.Model);
        GpuInstance row = resident is { } model ? CreateRow(Renderers.First[index], renderer, model, inputs) : default;
        row.PreviousWorld = isNew ? row.World : _rows[slot].World;
        _seen[slot] = _fill;
        _indexOf[slot] = index;
        _extras[slot] = resident is { Acceleration.IsNone: false } traced
            ? new InstanceExtra(gpu.GetAddress(traced.Acceleration), traced.LeafOffset)
            : default;
        if (resident is null && renderer.Model != Handle<Model>.None && !_isWaiting[slot])
        {
            _isWaiting[slot] = true;
            _waiting.Add(slot);
        }

        if (_rows[slot] == row)
            return slot;

        if (row.World != row.PreviousWorld)
            _moved.Add(slot);

        _rows[slot] = row;
        MarkDirty(slot);

        return slot;
    }

    /// <summary>
    /// The entity's slot, a free one taken when it has none; false, logged once, when the table is full.
    /// </summary>
    private bool TryGetSlot(Entity entity, out int slot, out bool isNew)
    {
        isNew = !_slotOf.TryGetValue(entity, out slot);
        if (!isNew)
            return true;

        if (!TryAllocate(out slot))
        {
            if (!_isFullLogged)
                Log.Error($"Some renderers are not drawn: the instance table holds {Capacity}.");

            _isFullLogged = true;

            return false;
        }

        _slotOf[entity] = slot;
        _entityOf[slot] = entity;
        _isUsed[slot] = true;

        return true;
    }

    private bool TryAllocate(out int slot)
    {
        if (_free.TryPop(out slot))
            return true;

        if (Count >= Capacity)
            return false;

        slot = Count++;

        return true;
    }

    private void MarkDirty(int slot)
    {
        if (_isDirty[slot])
            return;

        _isDirty[slot] = true;
        _dirtySlots.Add(slot);
    }

    /// <summary>
    /// What a row is built from beside its renderer.
    /// </summary>
    private readonly record struct RowInputs(
        GeometryPools Pools,
        MaterialTable Materials,
        Handle<Material> FailureMaterial
    );
}
