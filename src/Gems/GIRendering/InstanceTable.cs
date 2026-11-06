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
using Magic.Worlds;

namespace Magic.GIRendering;

/// <summary>
/// The instance table: a row per renderer, at its index in a snapshot of the renderers. A frame's work is what changed
/// since the last snapshot, a block of rows at a time: a block the same byte for byte stays as it is, one that only moved,
/// as renderers were made or destroyed before it, moves its rows, and any other is built again. Every row is built again
/// once a model is made or fails. Blocks that changed are uploaded in runs; a still scene uploads nothing.
/// </summary>
internal sealed class InstanceTable(IGraphicsApi gpu, RenderGraph graph, World world) : IDisposable
{
    public const int Capacity = 1 << 18;

    /// <summary>
    /// How many rows are compared, moved and uploaded together: a still block is a few vector compares.
    /// </summary>
    private const int BlockSize = 64;

    private readonly GpuInstance[] _rows = new GpuInstance[Capacity];

    private readonly InstanceExtra[] _extras = new InstanceExtra[Capacity];

    /// <summary>
    /// Whether each block of rows differs from what <see cref="Upload"/> last copied.
    /// </summary>
    private readonly bool[] _isDirty = new bool[Capacity / BlockSize];

    /// <summary>
    /// The snapshot the last fill read.
    /// </summary>
    private EcsSnapshot<Transform, Renderer> _last = new();

    private int _lastCount;

    private int _settledModels = -1;

    private bool _isFullLogged;

    public GpuBuffer Buffer { get; private set; }

    /// <summary>
    /// How many rows the cull walks: the renderers of the last fill, up to <see cref="Capacity"/>.
    /// </summary>
    public int Count { get; private set; }

    /// <summary>
    /// Whether the rows differ from what <see cref="Upload"/> last copied.
    /// </summary>
    public bool HasChanges { get; private set; }

    /// <summary>
    /// The renderers the next <see cref="Fill"/> reads, for the ECS to snapshot into.
    /// </summary>
    public EcsSnapshot<Transform, Renderer> Renderers { get; private set; } = new();

    /// <summary>
    /// The rows as the GPU reads them, by instance index.
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
    /// The rows of <see cref="Renderers"/>, from what changed since the last fill: each its model's place in the pools
    /// and the material rows of its entity's shading, or an empty row while its model is being made. How many rows the
    /// cull walks.
    /// </summary>
    public int Fill(uint frameIndex, GeometryPools pools, MaterialTable materials, ShadingInstance? failure)
    {
        if (Renderers.Count > Capacity && !_isFullLogged)
        {
            Log.Error($"Some renderers are not drawn: the instance table holds {Capacity}.");
            _isFullLogged = true;
        }

        _lastCount = Count;
        Count = Math.Min(Renderers.Count, Capacity);
        HasChanges |= Count != _lastCount;
        bool isRebuild = pools.SettledCount != _settledModels;
        _settledModels = pools.SettledCount;
        RowInputs inputs = new(frameIndex, pools, materials, failure);
        int shift = Count - _lastCount;
        int blocks = (Count + BlockSize - 1) / BlockSize;

        // Rows move towards the end when renderers were made, so the blocks go from the end then, and from the start
        // otherwise: a block moves before the block it moves onto is written.
        for (int i = 0; i < blocks; i++)
        {
            int start = (shift > 0 ? blocks - 1 - i : i) * BlockSize;
            int length = Math.Min(BlockSize, Count - start);
            if (!isRebuild && IsSameAs(start, start, length))
                continue;

            if (!isRebuild && shift != 0 && IsSameAs(start, start - shift, length))
                MoveRows(start - shift, start, length);
            else
                SetRows(start, length, inputs);
        }

        (Renderers, _last) = (_last, Renderers);

        return Count;
    }

    /// <summary>
    /// Copies the blocks of rows that changed into the table, each run of them in one copy, in the frame's upload pass.
    /// </summary>
    public void Upload()
    {
        if (!HasChanges)
            return;

        int blocks = (Count + BlockSize - 1) / BlockSize;
        int dirtyRows = 0;
        for (int counted = 0; counted < blocks; counted++)
        {
            if (_isDirty[counted])
                dirtyRows += GetBlockLength(counted);
        }

        HasChanges = false;
        if (dirtyRows == 0)
            return;

        GpuUpload upload = gpu.BeginUpload(dirtyRows * GpuInstance.Bytes);
        Span<GpuInstance> staged = MemoryMarshal.Cast<byte, GpuInstance>(upload.Bytes);
        graph.Use(Buffer, GpuAccess.CopyDestination);
        int stagedRows = 0;
        int block = 0;
        while (block < blocks)
        {
            if (!_isDirty[block])
            {
                block++;
                continue;
            }

            int runStart = block * BlockSize;
            int runRows = 0;
            for (; block < blocks && _isDirty[block]; block++)
            {
                runRows += GetBlockLength(block);
            }

            _rows.AsSpan(runStart, runRows).CopyTo(staged[stagedRows..]);
            graph.Recorder.CopyBuffer(
                upload.Buffer,
                upload.Offset + ((ulong)stagedRows * GpuInstance.Bytes),
                Buffer,
                (ulong)runStart * GpuInstance.Bytes,
                (ulong)runRows * GpuInstance.Bytes
            );
            stagedRows += runRows;
        }

        Array.Clear(_isDirty);
    }

    private static bool IsSame<T>(ReadOnlySpan<T> now, ReadOnlySpan<T> last)
        where T : unmanaged => MemoryMarshal.AsBytes(now).SequenceEqual(MemoryMarshal.AsBytes(last));

    /// <summary>
    /// A renderer's row, with its model resident: where it is, its bounds in the world and the material rows of its
    /// entity's shading, every slot the failure's when the model failed.
    /// </summary>
    private GpuInstance CreateRow(Entity entity, Transform transform, ResidentModel resident, in RowInputs inputs)
    {
        Span<uint> materialRows = stackalloc uint[MaterialSlots.Capacity];
        for (int slot = 0; slot < MaterialSlots.Capacity; slot++)
        {
            materialRows[slot] = inputs.Materials.GetRow(
                resident.IsFailure ? inputs.Failure : world.GetShading(entity, slot)
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
            PreviousWorld = worldMatrix,
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
    /// Whether the block of renderers at <paramref name="start"/> is byte for byte what the last snapshot had from
    /// <paramref name="lastStart"/>.
    /// </summary>
    private bool IsSameAs(int start, int lastStart, int length) =>
        lastStart >= 0
        && lastStart + length <= _lastCount
        && IsSame(Renderers.Entities.Slice(start, length), _last.Entities.Slice(lastStart, length))
        && IsSame(Renderers.First.Slice(start, length), _last.First.Slice(lastStart, length))
        && IsSame(Renderers.Second.Slice(start, length), _last.Second.Slice(lastStart, length));

    /// <summary>
    /// The rows of a block that only moved, from <paramref name="from"/> to <paramref name="to"/>.
    /// </summary>
    private void MoveRows(int from, int to, int length)
    {
        Array.Copy(_rows, from, _rows, to, length);
        Array.Copy(_extras, from, _extras, to, length);
        MarkDirty(to);
    }

    /// <summary>
    /// The rows of a block built again from its renderers. A renderer that was drawn at the same index and moved in the
    /// world keeps where it was, for this frame's motion.
    /// </summary>
    private void SetRows(int start, int length, in RowInputs inputs)
    {
        for (int index = start; index < start + length; index++)
        {
            Renderer renderer = Renderers.Second[index];
            ResidentModel? resident = renderer.Model == Handle<Model>.None ? null : inputs.Pools.Get(renderer.Model);
            GpuInstance row = resident is { } model
                ? CreateRow(Renderers.Entities[index], Renderers.First[index], model, inputs)
                : default;
            bool wasDrawnHere =
                index < _lastCount
                && _last.Entities[index] == Renderers.Entities[index]
                && _rows[index].ClusterCount > 0;
            if (wasDrawnHere && _rows[index].World != row.World)
            {
                row.PreviousWorld = _rows[index].World;
                row.MovedFrame = inputs.FrameIndex;
            }

            _rows[index] = row;
            _extras[index] = resident is { Acceleration.IsNone: false } traced
                ? new InstanceExtra(gpu.GetAddress(traced.Acceleration), traced.LeafOffset)
                : default;
        }

        MarkDirty(start);
    }

    private void MarkDirty(int start)
    {
        _isDirty[start / BlockSize] = true;
        HasChanges = true;
    }

    private int GetBlockLength(int block) => Math.Min(BlockSize, Count - (block * BlockSize));

    /// <summary>
    /// What a row is built from beside its renderer: <paramref name="Failure"/> the shading a failed model is drawn
    /// with, null when there is none.
    /// </summary>
    private readonly record struct RowInputs(
        uint FrameIndex,
        GeometryPools Pools,
        MaterialTable Materials,
        ShadingInstance? Failure
    );
}
