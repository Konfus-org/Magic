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
/// The instance table: a row per renderer, at its index in a snapshot of the renderers. A frame's work is what changed
/// since the last snapshot, a block of rows at a time: a block the same byte for byte stays as it is, one that only moved,
/// as renderers were made or destroyed before it, moves its rows, and any other is built again. Every row is built again
/// once a model is made or fails. Blocks that changed are uploaded in runs; a still scene uploads nothing. Beside it, the
/// material table: a row per distinct <see cref="MaterialInstance"/> drawn, which every slot holding it shares, made
/// again each fill while its material or texture loads, and uploaded whole in the frame a row changes.
/// </summary>
internal sealed class InstanceTable(IGraphicsApi gpu, Assets assets) : IDisposable
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
    /// The material rows, as many as there are instance rows, which holds every drawn slot of a world whose renderers
    /// fill one each; row 0 the default an empty slot is drawn with.
    /// </summary>
    private readonly GpuMaterial[] _materialRows = new GpuMaterial[Capacity];

    /// <summary>
    /// The slot each material row is made from, by row.
    /// </summary>
    private readonly MaterialInstance[] _materialSlots = new MaterialInstance[Capacity];

    private readonly Dictionary<MaterialInstance, uint> _materialRowOf = [];

    /// <summary>
    /// The material rows whose material or texture was loading when they were last made.
    /// </summary>
    private readonly List<uint> _loadingMaterialRows = [];

    /// <summary>
    /// The snapshot the last fill read.
    /// </summary>
    private EcsSnapshot<Transform, Renderer> _last = new();

    private int _lastCount;

    private int _settledModels = -1;

    private bool _isFullLogged;

    private int _materialCount = 1;

    /// <summary>
    /// Whether the material rows differ from what <see cref="Upload"/> last copied.
    /// </summary>
    private bool _hasMaterialChanges = true;

    public GpuBuffer Buffer { get; private set; }

    public GpuBuffer MaterialBuffer { get; private set; }

    /// <summary>
    /// How many rows the cull walks: the renderers of the last fill, up to <see cref="Capacity"/>.
    /// </summary>
    public int Count { get; private set; }

    /// <summary>
    /// Whether the rows differ from what <see cref="Upload"/> last copied.
    /// </summary>
    public bool HasChanges { get; private set; }

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
        gpu.Destroy(Buffer);
        gpu.Destroy(MaterialBuffer);
    }

    public Result Create()
    {
        Result<GpuBuffer> buffer = gpu.CreateBuffer("Instance table", (long)Capacity * GpuInstance.Bytes);
        if (!buffer.Ok)
            return Result.Failure(buffer.Error);

        Buffer = buffer.Value;
        _materialRows[0] = GpuResources.DefaultMaterial;
        Result<GpuBuffer> materials = gpu.CreateBuffer("Material table", (long)Capacity * GpuMaterial.Bytes);
        if (!materials.Ok)
            return Result.Failure(materials.Error);

        MaterialBuffer = materials.Value;

        return Result.Success();
    }

    /// <summary>
    /// The rows of <paramref name="renderers"/>, from what changed since the last fill: each its model's place in the
    /// pools and the material rows of its slots, or an empty row while its model is being made; and the material rows
    /// whose material or texture was loading, made again. How many rows the cull walks. The last fill's snapshot must be
    /// left as it was until this one.
    /// </summary>
    public int Fill(
        EcsSnapshot<Transform, Renderer> renderers,
        uint frameIndex,
        GeometryPools pools,
        GpuResources resources,
        in MaterialInstance failure
    )
    {
        for (int i = _loadingMaterialRows.Count - 1; i >= 0; i--)
        {
            if (SetMaterialRow(_loadingMaterialRows[i], resources) != ResourceState.Loading)
                _loadingMaterialRows.RemoveAt(i);
        }

        // The same snapshot again, with the same models, makes the same rows.
        bool isRebuild = pools.SettledCount != _settledModels;
        if (renderers == _last && !isRebuild)
            return Count;

        if (renderers.Count > Capacity && !_isFullLogged)
        {
            Log.Error($"Some renderers are not drawn: the instance table holds {Capacity}.");
            _isFullLogged = true;
        }

        _lastCount = Count;
        Count = Math.Min(renderers.Count, Capacity);
        HasChanges |= Count != _lastCount;
        _settledModels = pools.SettledCount;
        RowInputs inputs = new(renderers, frameIndex, pools, resources, failure);
        int shift = Count - _lastCount;
        int blocks = (Count + BlockSize - 1) / BlockSize;

        // Rows move towards the end when renderers were made, so the blocks go from the end then, and from the start
        // otherwise: a block moves before the block it moves onto is written.
        for (int i = 0; i < blocks; i++)
        {
            int start = (shift > 0 ? blocks - 1 - i : i) * BlockSize;
            int length = Math.Min(BlockSize, Count - start);
            if (!isRebuild && IsSameAs(renderers, start, start, length))
                continue;

            if (!isRebuild && shift != 0 && IsSameAs(renderers, start, start - shift, length))
                MoveRows(start - shift, start, length);
            else
                SetRows(start, length, inputs);
        }

        _last = renderers;

        return Count;
    }

    /// <summary>
    /// Copies the blocks of rows that changed into the table, each run of them in one copy, and the material rows into
    /// theirs when any changed, in the frame's upload pass.
    /// </summary>
    public void Upload(RenderGraph graph)
    {
        if (_hasMaterialChanges)
        {
            _hasMaterialChanges = false;
            graph.Upload(gpu, MaterialBuffer, _materialRows.AsSpan(0, _materialCount));
        }

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
            graph.CopyBuffer(
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
    /// slots, every slot the failure's when the model failed.
    /// </summary>
    private GpuInstance CreateRow(
        Transform transform,
        in Renderer renderer,
        ResidentModel resident,
        in RowInputs inputs
    )
    {
        Matrix4x4 worldMatrix = transform.Matrix;
        Matrix4x4.Invert(worldMatrix, out Matrix4x4 inverseWorld);
        Vector3 scale = transform.Scale;
        float maxScale = Math.Max(Math.Abs(scale.X), Math.Max(Math.Abs(scale.Y), Math.Abs(scale.Z)));
        GpuInstance row = new()
        {
            World = worldMatrix,
            InverseWorld = inverseWorld,
            PreviousWorld = worldMatrix,
            BoundsCenter = Vector3.Transform(resident.BoundsCenter, worldMatrix),
            BoundsRadius = resident.BoundsRadius * maxScale,
            ClusterOffset = resident.ClusterOffset,
            ClusterCount = resident.ClusterCount,
        };
        for (int slot = 0; slot < MaterialSlots.Capacity; slot++)
        {
            row.Materials[slot] = GetMaterialRow(
                resident.IsFailure ? inputs.Failure : renderer.Materials[slot],
                inputs.Resources
            );
        }

        return row;
    }

    /// <summary>
    /// Whether the block of <paramref name="renderers"/> at <paramref name="start"/> is byte for byte what the last
    /// snapshot had from <paramref name="lastStart"/>.
    /// </summary>
    private bool IsSameAs(EcsSnapshot<Transform, Renderer> renderers, int start, int lastStart, int length) =>
        lastStart >= 0
        && lastStart + length <= _lastCount
        && IsSame(renderers.Entities.Slice(start, length), _last.Entities.Slice(lastStart, length))
        && IsSame(renderers.First.Slice(start, length), _last.First.Slice(lastStart, length))
        && IsSame(renderers.Second.Slice(start, length), _last.Second.Slice(lastStart, length));

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
            Renderer renderer = inputs.Renderers.Second[index];
            ResidentModel? resident = renderer.Model == Handle<Model>.None ? null : inputs.Pools.Get(renderer.Model);
            GpuInstance row = resident is { } model
                ? CreateRow(inputs.Renderers.First[index], renderer, model, inputs)
                : default;
            bool wasDrawnHere =
                index < _lastCount
                && _last.Entities[index] == inputs.Renderers.Entities[index]
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

    /// <summary>
    /// The slot's material row, 0 for an empty slot; a value first seen takes a row, which every slot holding it shares.
    /// A slot whose params change takes another row; rows are never given back.
    /// </summary>
    private uint GetMaterialRow(in MaterialInstance slot, GpuResources resources)
    {
        if (slot.Handle == Handle<Material>.None)
            return 0;

        if (_materialRowOf.TryGetValue(slot, out uint row))
            return row;

        if (_materialCount >= Capacity)
        {
            Log.Error($"{assets.GetPath(slot.Handle)} is drawn as the default material: the material table is full.");
            _materialRowOf[slot] = 0;

            return 0;
        }

        row = (uint)_materialCount++;
        _materialSlots[row] = slot;
        _materialRowOf[slot] = row;
        if (SetMaterialRow(row, resources) == ResourceState.Loading)
            _loadingMaterialRows.Add(row);

        return row;
    }

    /// <summary>
    /// Makes the material row from its slot, as the GPU resources have its material now; whether that still loads.
    /// </summary>
    private ResourceState SetMaterialRow(uint row, GpuResources resources)
    {
        ResourceState state = resources.Get(_materialSlots[row], out GpuMaterial made);
        if (_materialRows[row] == made)
            return state;

        _materialRows[row] = made;
        _hasMaterialChanges = true;

        return state;
    }

    private void MarkDirty(int start)
    {
        _isDirty[start / BlockSize] = true;
        HasChanges = true;
    }

    private int GetBlockLength(int block) => Math.Min(BlockSize, Count - (block * BlockSize));

    /// <summary>
    /// What a row is built from beside its renderer: <paramref name="Failure"/> the material a failed model is drawn
    /// with, an empty slot when there is none.
    /// </summary>
    private readonly record struct RowInputs(
        EcsSnapshot<Transform, Renderer> Renderers,
        uint FrameIndex,
        GeometryPools Pools,
        GpuResources Resources,
        MaterialInstance Failure
    );
}
