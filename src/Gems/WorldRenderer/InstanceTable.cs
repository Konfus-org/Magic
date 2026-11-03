using System.Runtime.InteropServices;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Gpu;
using Magic.Rendering;
using Magic.Utils;

namespace WorldRenderer;

/// <summary>
/// The instance table: a slot per entity drawn, kept from frame to frame, so a frame's work is the rows that changed:
/// an entity that moved, arrived or left. Rows that changed are uploaded in runs; a still scene uploads nothing.
/// </summary>
internal sealed class InstanceTable(IGraphicsApi gpu) : IDisposable
{
    public const int Capacity = 1 << 18;

    private readonly GpuInstance[] _rows = new GpuInstance[Capacity];
    private readonly Entity[] _entityOf = new Entity[Capacity];
    private readonly bool[] _isUsed = new bool[Capacity];
    private readonly bool[] _isDirty = new bool[Capacity];
    private readonly uint[] _seen = new uint[Capacity];
    private readonly Dictionary<Entity, int> _slotOf = [];
    private readonly Stack<int> _free = new();
    private readonly List<int> _dirtySlots = [];
    private uint _frame;
    private bool _isFullLogged;

    public GpuBuffer Buffer { get; private set; }

    /// <summary>
    /// One past the last slot in use: how many rows the cull walks.
    /// </summary>
    public int Count { get; private set; }

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
    /// Starts a frame's rows: every entity set before <see cref="End"/> keeps its slot, the others lose theirs.
    /// </summary>
    public void Begin(uint frame) => _frame = frame;

    /// <summary>
    /// The entity's row this frame, uploaded when it differs from the last.
    /// </summary>
    public void Set(Entity entity, in GpuInstance row)
    {
        if (!_slotOf.TryGetValue(entity, out int slot))
        {
            if (!TryAllocate(out slot))
            {
                if (!_isFullLogged)
                    Log.Error($"Some renderers are not drawn: the instance table holds {Capacity}.");

                _isFullLogged = true;

                return;
            }

            _slotOf[entity] = slot;
            _entityOf[slot] = entity;
            _isUsed[slot] = true;
        }

        _seen[slot] = _frame;
        if (_rows[slot] == row)
            return;

        _rows[slot] = row;
        MarkDirty(slot);
    }

    /// <summary>
    /// Frees the slots of the entities not set this frame; their rows draw nothing from now on.
    /// </summary>
    public void End()
    {
        for (int slot = 0; slot < Count; slot++)
        {
            if (!_isUsed[slot] || _seen[slot] == _frame)
                continue;

            _slotOf.Remove(_entityOf[slot]);
            _isUsed[slot] = false;
            _free.Push(slot);
            _rows[slot] = default;
            MarkDirty(slot);
        }
    }

    /// <summary>
    /// Copies the rows that changed into the table, each run of them in one copy, in the frame's upload pass.
    /// </summary>
    public void Upload(RenderGraph graph)
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
}
