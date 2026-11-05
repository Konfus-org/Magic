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
/// The instance table: a slot per entity drawn, kept from frame to frame, so a frame's work is the rows that changed:
/// an entity that moved, arrived or left. Rows that changed are uploaded in runs; a still scene uploads nothing.
/// </summary>
internal sealed class InstanceTable(IGraphicsApi gpu, RenderGraph graph) : IDisposable
{
    public const int Capacity = 1 << 18;

    private readonly GpuInstance[] _rows = new GpuInstance[Capacity];

    private readonly InstanceExtra[] _extras = new InstanceExtra[Capacity];

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

    /// <summary>
    /// Whether any row differs from what <see cref="Upload"/> last copied.
    /// </summary>
    public bool HasChanges => _dirtySlots.Count > 0;

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
    /// Every renderer's row in the table: its model's place in the pools and its material rows, or an empty row while its
    /// model is being made. How many rows the cull walks.
    /// </summary>
    public int Fill(RenderWorld world, GeometryPools pools, MaterialTable materials, Handle<Material> failureMaterial)
    {
        Span<uint> materialRows = stackalloc uint[MaterialSlots.Capacity];
        Begin(unchecked((uint)Environment.TickCount));
        foreach (RenderInstance instance in world.Instances)
        {
            if (pools.Get(instance.Model) is not { } resident)
            {
                Set(instance.Entity, default);
                continue;
            }

            MaterialSlots slots = instance.Materials;
            ReadOnlySpan<Handle<Material>> slotHandles = slots;
            for (int slot = 0; slot < MaterialSlots.Capacity; slot++)
            {
                materialRows[slot] = materials.GetRow(resident.IsFailure ? failureMaterial : slotHandles[slot]);
            }

            Matrix4x4 worldMatrix = instance.Transform.Matrix;
            Matrix4x4.Invert(worldMatrix, out Matrix4x4 inverseWorld);
            Vector3 scale = instance.Transform.Scale;
            float maxScale = Math.Max(Math.Abs(scale.X), Math.Max(Math.Abs(scale.Y), Math.Abs(scale.Z)));
            Vector3 worldCenter = Vector3.Transform(resident.BoundsCenter, worldMatrix);
            float worldRadius = resident.BoundsRadius * maxScale;
            InstanceExtra extra = resident.Acceleration.IsNone
                ? default
                : new InstanceExtra(gpu.GetAddress(resident.Acceleration), resident.LeafOffset);
            Set(
                instance.Entity,
                new GpuInstance
                {
                    World = worldMatrix,
                    InverseWorld = inverseWorld,
                    BoundsCenter = worldCenter,
                    BoundsRadius = worldRadius,
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
                },
                extra
            );
        }

        End();

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

    /// <summary>
    /// Starts a frame's rows: every entity set before <see cref="End"/> keeps its slot, the others lose theirs.
    /// </summary>
    private void Begin(uint frame) => _frame = frame;

    /// <summary>
    /// The entity's row this frame, uploaded when it differs from the last; its previous world is what its row held
    /// last frame, so a thing that moved carries where it was.
    /// </summary>
    private void Set(Entity entity, in GpuInstance given, in InstanceExtra extra = default)
    {
        GpuInstance row = given;
        bool isNew = !_slotOf.TryGetValue(entity, out int slot);
        if (isNew)
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

        row.PreviousWorld = isNew ? row.World : _rows[slot].World;
        _seen[slot] = _frame;
        _extras[slot] = extra;
        if (_rows[slot] == row)
            return;

        _rows[slot] = row;
        MarkDirty(slot);
    }

    /// <summary>
    /// Frees the slots of the entities not set this frame; their rows draw nothing from now on.
    /// </summary>
    private void End()
    {
        for (int slot = 0; slot < Count; slot++)
        {
            if (!_isUsed[slot] || _seen[slot] == _frame)
                continue;

            _slotOf.Remove(_entityOf[slot]);
            _isUsed[slot] = false;
            _free.Push(slot);
            _rows[slot] = default;
            _extras[slot] = default;
            MarkDirty(slot);
        }
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
