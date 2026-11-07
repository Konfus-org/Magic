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
/// The instance table: a row per renderer, at its index in a snapshot of the renderers. A frame's work is the chunks of
/// renderers written since the snapshot the last fill read, or that moved: their rows are built again, each renderer
/// asking for its model, and the rest stay as they are. Every row is built again once a model is made or fails. Blocks
/// of rows that changed are uploaded in runs; a still scene uploads none. Beside it, the material table: a row per
/// distinct <see cref="MaterialInstance"/> drawn, which every slot holding it shares, made again each frame from what
/// <paramref name="resources"/> has then, and uploaded whole. <paramref name="failure"/> is the material a model that
/// failed is drawn with, every slot; an empty slot when there is none.
/// </summary>
internal sealed class InstanceTable(IGraphicsApi gpu, Assets assets, GpuResources resources, MaterialInstance failure)
{
    public const int Capacity = 1 << 18;

    /// <summary>
    /// How many rows are marked changed and uploaded together.
    /// </summary>
    private const int BlockSize = 64;

    private readonly GpuInstance[] _rows = new GpuInstance[Capacity];

    private readonly InstanceExtra[] _extras = new InstanceExtra[Capacity];

    /// <summary>
    /// Whether each block of rows differs from what <see cref="Upload"/> last copied.
    /// </summary>
    private readonly bool[] _isDirty = new bool[Capacity / BlockSize];

    /// <summary>
    /// The entity each row was written from, by row: what a renderer that moved carries where it was from.
    /// </summary>
    private readonly Entity[] _entities = new Entity[Capacity];

    /// <summary>
    /// The chunks of the snapshot the last fill read, and the version it was filled at.
    /// </summary>
    private readonly List<EcsSnapshotChunk> _filled = [];

    /// <summary>
    /// The material rows, as many as there are instance rows, which holds every drawn slot of a world whose renderers
    /// fill one each; row 0 the default an empty slot is drawn with.
    /// </summary>
    private readonly GpuMaterial[] _materialRows = CreateMaterialRows();

    /// <summary>
    /// The slot each material row is made from, by row; row 0 the empty slot.
    /// </summary>
    private readonly List<MaterialInstance> _materialSlots = [default];

    private readonly Dictionary<MaterialInstance, uint> _materialRowOf = [];

    private long _filledAt;

    private int _settledModels = -1;

    private bool _isFullLogged;

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

    /// <summary>
    /// The rows of <paramref name="renderers"/>, from what changed since the last fill: each its model's place in the
    /// pools and the material rows of its slots, or an empty row while its model is being made. How many rows the cull
    /// walks.
    /// </summary>
    public int Fill(EcsSnapshot<Transform, Renderer> renderers, uint frameIndex)
    {
        // The same snapshot again, with the same models, makes the same rows.
        bool isRebuild = resources.SettledModels != _settledModels;
        if (renderers.Version == _filledAt && !isRebuild)
            return Count;

        if (renderers.Count > Capacity && !_isFullLogged)
        {
            Log.Error($"Some renderers are not drawn: the instance table holds {Capacity}.");
            _isFullLogged = true;
        }

        int lastCount = Count;
        Count = Math.Min(renderers.Count, Capacity);
        HasChanges |= Count != lastCount;
        _settledModels = resources.SettledModels;

        // A chunk the last fill read in the same place, and not written since, has its rows already.
        ReadOnlySpan<EcsSnapshotChunk> chunks = renderers.Chunks;
        for (int i = 0; i < chunks.Length && chunks[i].Offset < Count; i++)
        {
            EcsSnapshotChunk chunk = chunks[i];
            bool isBuilt =
                !isRebuild && i < _filled.Count && _filled[i].IsSameChunk(chunk) && chunk.Written <= _filledAt;
            if (!isBuilt)
                SetRows(renderers, chunk.Offset, Math.Min(chunk.Count, Count - chunk.Offset), lastCount, frameIndex);
        }

        _filled.Clear();
        _filled.AddRange(chunks);
        _filledAt = renderers.Version;

        return Count;
    }

    /// <summary>
    /// The table and the material table asked for; the blocks of rows that changed copied into the table, each run of
    /// them in one copy, and the material rows made again from what the GPU resources have now and copied into theirs,
    /// in the frame's upload pass.
    /// </summary>
    public Result<(GpuBuffer Rows, GpuBuffer Materials)> Upload(RenderGraph graph)
    {
        Result<GpuBuffer> rows = resources.TryGetBuffer("Instance table", (long)Capacity * GpuInstance.Bytes);
        if (!rows.Ok)
            return Result.Failure(rows.Error);

        Result<GpuBuffer> materials = resources.TryGetBuffer("Material table", (long)Capacity * GpuMaterial.Bytes);
        if (!materials.Ok)
            return Result.Failure(materials.Error);

        // Every material row made again, so a material or texture that arrived shows with no renderer written.
        for (int row = 1; row < _materialSlots.Count; row++)
        {
            resources.Get(_materialSlots[row], out _materialRows[row]);
        }

        graph.Upload(gpu, materials.Value, _materialRows.AsSpan(0, _materialSlots.Count));
        UploadChangedRows(graph, rows.Value);

        return Result.Success((rows.Value, materials.Value));
    }

    /// <summary>
    /// The material rows, row 0 the default an empty slot is drawn with.
    /// </summary>
    private static GpuMaterial[] CreateMaterialRows()
    {
        GpuMaterial[] rows = new GpuMaterial[Capacity];
        rows[0] = GpuResources.DefaultMaterial;

        return rows;
    }

    /// <summary>
    /// Copies the blocks of rows that changed into <paramref name="table"/>, each run of them in one copy.
    /// </summary>
    private void UploadChangedRows(RenderGraph graph, GpuBuffer table)
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
        graph.Use(table, GpuAccess.CopyDestination);
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
                table,
                (ulong)runStart * GpuInstance.Bytes,
                (ulong)runRows * GpuInstance.Bytes
            );
            stagedRows += runRows;
        }

        Array.Clear(_isDirty);
    }

    /// <summary>
    /// The rows from <paramref name="start"/> built again from their renderers, each asking for its model. A renderer
    /// drawn at the same index in the last fill that moved in the world keeps where it was, for this frame's motion.
    /// </summary>
    private void SetRows(
        EcsSnapshot<Transform, Renderer> renderers,
        int start,
        int length,
        int lastCount,
        uint frameIndex
    )
    {
        for (int index = start; index < start + length; index++)
        {
            Entity entity = renderers.Entities[index];
            Renderer renderer = renderers.Second[index];
            ResidentModel? resident = renderer.Model == Handle<Model>.None ? null : resources.Get(renderer.Model);
            GpuInstance row = resident is { } model ? CreateRow(renderers.First[index], renderer, model) : default;
            bool wasDrawnHere = index < lastCount && _entities[index] == entity && _rows[index].ClusterCount > 0;
            if (wasDrawnHere && _rows[index].World != row.World)
            {
                row.PreviousWorld = _rows[index].World;
                row.MovedFrame = frameIndex;
            }

            _rows[index] = row;
            _entities[index] = entity;
            _extras[index] = resident is { Acceleration.IsNone: false } traced
                ? new InstanceExtra(gpu.GetAddress(traced.Acceleration), traced.LeafOffset)
                : default;
        }

        for (int block = start / BlockSize; block <= (start + length - 1) / BlockSize; block++)
        {
            _isDirty[block] = true;
        }

        HasChanges = true;
    }

    /// <summary>
    /// A renderer's row, with its model resident: where it is, its bounds in the world and the material rows of its
    /// slots, every slot the failure's when the model failed.
    /// </summary>
    private GpuInstance CreateRow(Transform transform, in Renderer renderer, ResidentModel resident)
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
            row.Materials[slot] = GetMaterialRow(resident.IsFailure ? failure : renderer.Materials[slot]);
        }

        return row;
    }

    /// <summary>
    /// The slot's material row, 0 for an empty slot; a value first seen takes the next row, which every slot holding it
    /// shares. A slot whose params change takes another row; rows are never given back.
    /// </summary>
    private uint GetMaterialRow(in MaterialInstance slot)
    {
        if (slot.Handle == Handle<Material>.None)
            return 0;

        if (_materialRowOf.TryGetValue(slot, out uint row))
            return row;

        if (_materialSlots.Count >= Capacity)
        {
            Log.Error($"{assets.GetPath(slot.Handle)} is drawn as the default material: the material table is full.");
            _materialRowOf[slot] = 0;

            return 0;
        }

        row = (uint)_materialSlots.Count;
        _materialSlots.Add(slot);
        _materialRowOf[slot] = row;

        return row;
    }

    private int GetBlockLength(int block) => Math.Min(BlockSize, Count - (block * BlockSize));
}
