using System.Numerics;
using Magic.Assets;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Graphics;
using Magic.Utils;

namespace Magic.GpuWorld;

/// <summary>
/// The instance table: a row per renderer, at its index in a snapshot of the renderers. A frame's work is the chunks of
/// renderers written since the snapshot the last fill read, or that moved: their rows are built again, each renderer
/// asking for its model, and the rest stay as they are. Every row is built again once a model is made or fails. Blocks
/// of rows that changed are uploaded in runs; a still scene uploads none. Beside it, the material table: a row per
/// distinct <see cref="MaterialInstance"/> drawn, which every slot holding it shares, made again each frame from what
/// the GPU resources have then, and uploaded whole. A model that failed, or a material that did not load, is drawn with
/// the failure material; an empty slot when there is none. Row 0 is the default
/// material, which an empty slot and a material still loading are drawn with. Public for the gems that build from the
/// rows, on the render thread, after the world's gem has filled them for the frame.
/// </summary>
public sealed class InstanceTable
{
    public static readonly int Capacity = (int)(SharedResources.Instances.Bytes / GpuInstance.Bytes);

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
    /// fill one each.
    /// </summary>
    private readonly GpuMaterial[] _materialRows = new GpuMaterial[Capacity];

    /// <summary>
    /// The slot each material row is made from, by row; row 0 the default material's.
    /// </summary>
    private readonly List<MaterialInstance> _materialSlots;

    private readonly Dictionary<MaterialInstance, uint> _materialRowOf = [];

    private long _filledAt;

    private readonly AssetManager _assets;

    private readonly GpuResources _resources;

    private readonly MaterialInstance _failure;

    private int _settledModels = -1;

    /// <summary>
    /// The <see cref="Version"/> of the rows <see cref="Upload"/> last copied.
    /// </summary>
    private long _uploadedVersion;

    private bool _isFullLogged;

    internal InstanceTable(AssetManager assets, GpuResources resources, MaterialInstance failure)
    {
        _assets = assets;
        _resources = resources;
        _failure = failure;
        _materialSlots = [new() { Handle = assets.GetDefault<Material>() }];
    }

    /// <summary>
    /// How many rows the cull walks: the renderers of the last fill, up to <see cref="Capacity"/>.
    /// </summary>
    internal int Count { get; private set; }

    /// <summary>
    /// How many times the rows have changed: what is built from them is built again when it differs from the count it
    /// was built at.
    /// </summary>
    public long Version { get; private set; }

    /// <summary>
    /// The rows as the GPU reads them, by instance index.
    /// </summary>
    public ReadOnlySpan<GpuInstance> Rows => _rows.AsSpan(0, Count);

    /// <summary>
    /// What the CPU keeps beside each row, by instance index.
    /// </summary>
    public ReadOnlySpan<InstanceExtra> Extras => _extras.AsSpan(0, Count);

    /// <summary>
    /// How many rows the material table holds: one per distinct slot drawn, the default material's first.
    /// </summary>
    public int MaterialCount => _materialSlots.Count;

    /// <summary>
    /// The rows of <paramref name="renderers"/>, from what changed since the last fill: each its model's place in the
    /// pools and the material rows of its slots, or an empty row while its model is being made. How many rows the cull
    /// walks.
    /// </summary>
    internal int Fill(EcsSnapshot<Transform, Renderer> renderers, uint frameIndex)
    {
        // The same snapshot again, with the same models, makes the same rows.
        bool isRebuild = _resources.SettledModels != _settledModels;
        if (renderers.Version == _filledAt && !isRebuild)
            return Count;

        if (renderers.Count > Capacity && !_isFullLogged)
        {
            Log.Error($"Some renderers are not drawn: the instance table holds {Capacity}.");
            _isFullLogged = true;
        }

        int lastCount = Count;
        Count = Math.Min(renderers.Count, Capacity);
        if (Count != lastCount)
            Version++;

        _settledModels = _resources.SettledModels;

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
    internal Result<(GpuBuffer Rows, GpuBuffer Materials)> Upload(RenderGraph graph)
    {
        Result<GpuBuffer> rows = graph.Buffer(SharedResources.Instances);
        if (!rows.Ok)
            return Result.Failure(rows.Error);

        Result<GpuBuffer> materials = graph.Buffer(
            GpuDescriptions.GetBuffer("Material table", (long)Capacity * GpuMaterial.Bytes)
        );
        if (!materials.Ok)
            return Result.Failure(materials.Error);

        // Every material row made again, so a material or texture that arrived shows with no renderer written: one still
        // loading as the default, row 0, and one that failed as the failure.
        for (int row = 0; row < _materialSlots.Count; row++)
        {
            ResourceState state = _resources.Get(_materialSlots[row], out _materialRows[row]);
            if (state == ResourceState.Loading)
                _materialRows[row] = _materialRows[0];
            else if (
                state == ResourceState.Failed
                && _resources.Get(_failure, out GpuMaterial failed) == ResourceState.Failed
            )
                _materialRows[row] = failed;
        }

        graph.Upload(materials.Value, _materialRows.AsSpan(0, _materialSlots.Count));
        UploadChangedRows(graph, rows.Value);

        return Result.Success((rows.Value, materials.Value));
    }

    /// <summary>
    /// The box in the world around a box in a model's frame placed by <paramref name="world"/>: its centre placed, and
    /// its half extent along each world axis the sum of how far each of the model's axes reaches along it.
    /// </summary>
    private static (Vector3 Min, Vector3 Max) ToWorldBox(Vector3 min, Vector3 max, Matrix4x4 world)
    {
        Vector3 center = Vector3.Transform((min + max) / 2, world);
        Vector3 extent = (max - min) / 2;
        Vector3 reach = new(
            (Math.Abs(world.M11) * extent.X) + (Math.Abs(world.M21) * extent.Y) + (Math.Abs(world.M31) * extent.Z),
            (Math.Abs(world.M12) * extent.X) + (Math.Abs(world.M22) * extent.Y) + (Math.Abs(world.M32) * extent.Z),
            (Math.Abs(world.M13) * extent.X) + (Math.Abs(world.M23) * extent.Y) + (Math.Abs(world.M33) * extent.Z)
        );

        return (center - reach, center + reach);
    }

    /// <summary>
    /// Copies the blocks of rows that changed into <paramref name="table"/>, each run of them in one copy.
    /// </summary>
    private void UploadChangedRows(RenderGraph graph, GpuBuffer table)
    {
        if (Version == _uploadedVersion)
            return;

        _uploadedVersion = Version;
        int blocks = (Count + BlockSize - 1) / BlockSize;
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

            graph.Upload(table, _rows.AsSpan(runStart, runRows), (ulong)runStart * GpuInstance.Bytes);
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
            ResidentModel? resident = renderer.Model == Handle<Model>.None ? null : _resources.Get(renderer.Model);
            GpuInstance row = resident is { } model ? CreateRow(renderers.First[index], renderer, model) : default;
            bool wasDrawnHere = index < lastCount && _entities[index] == entity && _rows[index].Model != 0;
            if (wasDrawnHere && _rows[index].World != row.World)
            {
                row.PreviousWorld = _rows[index].World;
                row.MovedFrame = frameIndex;
            }

            _rows[index] = row;
            _entities[index] = entity;
            _extras[index] = resident is { Acceleration.IsNone: false } traced
                ? new InstanceExtra(
                    traced.Acceleration.Address,
                    traced.MeshOffset,
                    ToWorldBox(traced.BoundsMin, traced.BoundsMax, renderers.First[index].Matrix)
                )
                : default;
        }

        for (int block = start / BlockSize; block <= (start + length - 1) / BlockSize; block++)
        {
            _isDirty[block] = true;
        }

        Version++;
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
            Model = resident.ModelIndex,
        };
        for (int slot = 0; slot < MaterialSlots.Capacity; slot++)
        {
            row.Materials[slot] = GetMaterialRow(resident.IsFailure ? _failure : renderer.Materials[slot]);
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
            Log.Error($"{_assets.GetPath(slot.Handle)} is drawn as the default material: the material table is full.");
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

/// <summary>
/// What the CPU keeps beside an instance's row: its model's acceleration structure's address, 0 when it has none,
/// where its meshes start in the mesh table, and the box around it in the world.
/// </summary>
public readonly record struct InstanceExtra(ulong Acceleration, uint MeshOffset, (Vector3 Min, Vector3 Max) Box);
