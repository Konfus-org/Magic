using System.Collections.Immutable;
using System.Numerics;
using Magic.Assets;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Graphics;
using Magic.Utils;

namespace Magic.DeferredRendering.World;

/// <summary>
/// The instance table: a row per renderer, at its index in a snapshot of the renderers. A frame's work is the chunks of
/// renderers written since the snapshot the last fill read, or that moved: their rows are built again, each renderer
/// asking for its model, and the rest stay as they are. Every row is built again once a model is made or fails. Blocks
/// of rows that changed are uploaded in runs; a still scene uploads none. Beside it, the material table: a row per
/// distinct <see cref="MaterialInstance"/> drawn, which every slot holding it shares, made again each frame from what
/// <paramref name="resources"/> has then, and uploaded whole. Row 0 is the default material, which an empty slot and a
/// material still loading are drawn with; a material that failed is drawn with row 0's params and shader, and the
/// index of its row in the failure table, which names it. <paramref name="failure"/> is the material a model that
/// failed is drawn with; an empty slot when there is none.
/// </summary>
internal sealed class InstanceTable(AssetManager assets, GpuResources resources, MaterialInstance failure)
{
    /// <summary>
    /// The most rows the table and the material table hold.
    /// </summary>
    public const int MaxCapacity = 1 << 18;

    /// <summary>
    /// How many rows are marked changed and uploaded together.
    /// </summary>
    private const int BlockSize = 64;

    /// <summary>
    /// The fewest rows a table holds, before it grows by doubling to hold the world's.
    /// </summary>
    private const int MinCapacity = 16 * BlockSize;

    private GpuInstance[] _rows = new GpuInstance[MinCapacity];

    /// <summary>
    /// Whether each block of rows differs from what <see cref="Upload"/> last copied.
    /// </summary>
    private bool[] _isDirty = new bool[MinCapacity / BlockSize];

    /// <summary>
    /// The entity each row was written from, by row: what a renderer that moved carries where it was from.
    /// </summary>
    private Entity[] _entities = new Entity[MinCapacity];

    /// <summary>
    /// The chunks of the snapshot the last fill read, and the version it was filled at.
    /// </summary>
    private readonly List<EcsSnapshotChunk> _filled = [];

    /// <summary>
    /// The material rows, a row for each distinct slot drawn, up to as many as there are instance rows at most, which
    /// holds every drawn slot of a world whose renderers fill one each.
    /// </summary>
    private GpuMaterial[] _materialRows = new GpuMaterial[MinCapacity];

    /// <summary>
    /// The slot each material row is made from, by row; row 0 the default material's.
    /// </summary>
    private readonly List<MaterialInstance> _materialSlots = [new() { Handle = assets.GetDefault<Material>() }];

    private readonly Dictionary<MaterialInstance, uint> _materialRowOf = [];

    /// <summary>
    /// The failure table's rows, a row for each material that failed, made once when it fails; row 0 is never read, as
    /// a failure index of 0 means no failure.
    /// </summary>
    private GpuFailure[] _failureRows = new GpuFailure[16];

    /// <summary>
    /// The row of each material that failed in the failure table, by the material's id.
    /// </summary>
    private readonly Dictionary<ulong, uint> _failureRowOf = [];

    private long _filledAt;

    private int _settledModels = -1;

    /// <summary>
    /// How many times the rows have changed.
    /// </summary>
    private long _version;

    /// <summary>
    /// How many rows the cull walks: the renderers of the last fill, up to <see cref="MaxCapacity"/>.
    /// </summary>
    public int Count { get; private set; }

    /// <summary>
    /// How many rows the table holds now: as many as the world's renderers fill, rounded up to a power of two.
    /// </summary>
    public int Capacity => _rows.Length;

    /// <summary>
    /// The frame's instances as the gems that build from them take them: the rows, how many rows the material table holds
    /// and has room for, and how many times the rows have changed.
    /// </summary>
    public GpuInstances Instances =>
        new(_rows.AsMemory(0, Count), _materialSlots.Count, _materialRows.Length, _version);

    /// <summary>
    /// The rows of <paramref name="renderers"/>, from what changed since the last fill: each its model's place in the
    /// pools and the material rows of its slots, or an empty row while its model is being made.
    /// </summary>
    public void Fill(EcsSnapshot<Transform, Ecs.Components.Renderer> renderers, uint frameIndex)
    {
        // The same snapshot again, with the same models, makes the same rows.
        bool isRebuild = resources.SettledModels != _settledModels;
        if (renderers.Version == _filledAt && !isRebuild)
            return;

        // The error shows each time the table fills, not each fill while it is full.
        int lastCount = Count;
        if (renderers.Count > MaxCapacity && lastCount < MaxCapacity)
            Log.Error($"Some renderers are not drawn: the instance table holds {MaxCapacity}.");

        Count = Math.Min(renderers.Count, MaxCapacity);
        if (Count != lastCount)
            _version++;

        // The table grows as the world does, the rows it has kept as they are.
        if (Count > _rows.Length)
        {
            int capacity = GetCapacity(Count);
            Array.Resize(ref _rows, capacity);
            Array.Resize(ref _entities, capacity);
            Array.Resize(ref _isDirty, capacity / BlockSize);
        }

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
    }

    /// <summary>
    /// The table, the material table and the failure table asked for; the blocks of rows that changed copied into the
    /// table, each run of them in one copy, the material rows made again from what the GPU resources have now and copied
    /// into theirs, and the failure rows copied into theirs, in the frame's upload pass.
    /// </summary>
    public Result<(GpuBuffer Rows, GpuBuffer Materials, GpuBuffer Failures)> Upload(RenderGraph graph)
    {
        // A table asked for bigger than last frame keeps what it held.
        Result<GpuBuffer> rows = graph.Buffer(new("Instance table", (long)_rows.Length * GpuInstance.Bytes));
        if (!rows.Ok)
            return Result.Failure(rows.Error);

        if (_materialSlots.Count > _materialRows.Length)
            Array.Resize(ref _materialRows, GetCapacity(_materialSlots.Count));

        Result<GpuBuffer> materials = graph.Buffer(
            new("Material table", (long)_materialRows.Length * GpuMaterial.Bytes)
        );
        if (!materials.Ok)
            return Result.Failure(materials.Error);

        // Every material row made again, so a material or texture that arrived shows with no renderer written: one still
        // loading as the default, row 0, and one that failed as the default with its failure; the failure material, which
        // a model that failed is drawn with, as itself.
        for (int row = 0; row < _materialSlots.Count; row++)
        {
            Handle<Material> material = _materialSlots[row].Handle;
            GpuState state = resources.Get(_materialSlots[row], out _materialRows[row]);
            if (state == GpuState.Loading)
                _materialRows[row] = _materialRows[0];
            else if (state == GpuState.Failed && material != failure.Handle)
            {
                uint failed = GetFailureRow(material);
                _materialRows[row] = _materialRows[0];
                _materialRows[row].Shader =
                    (_materialRows[0].Shader & ~(GpuMaterial.FailureMask << GpuMaterial.FailureShift))
                    | (failed << GpuMaterial.FailureShift);
            }
        }

        graph.Upload(materials.Value, _materialRows.AsSpan(0, _materialSlots.Count));

        // The failure table, asked for after the material rows made its rows, so each row they name is in it.
        Result<GpuBuffer> failures = graph.Buffer(new("Failure table", (long)_failureRows.Length * GpuFailure.Bytes));
        if (!failures.Ok)
            return Result.Failure(failures.Error);

        graph.Upload(failures.Value, _failureRows.AsSpan(0, _failureRowOf.Count + 1));

        // The blocks of rows that changed, each run of them in one copy.
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
                runRows += Math.Min(BlockSize, Count - (block * BlockSize));
            }

            graph.Upload(rows.Value, _rows.AsSpan(runStart, runRows), (ulong)runStart * GpuInstance.Bytes);
        }

        Array.Clear(_isDirty);

        return Result.Success((rows.Value, materials.Value, failures.Value));
    }

    /// <summary>
    /// How many rows a table holds to hold <paramref name="rows"/>: the next power of two, from
    /// <see cref="MinCapacity"/> up to <see cref="MaxCapacity"/>.
    /// </summary>
    private static int GetCapacity(int rows) =>
        (int)Math.Min(BitOperations.RoundUpToPowerOf2((uint)Math.Max(rows, MinCapacity)), MaxCapacity);

    /// <summary>
    /// The rows from <paramref name="start"/> built again from their renderers, each asking for its model. A renderer
    /// drawn at the same index in the last fill that moved in the world keeps where it was, for this frame's motion.
    /// </summary>
    private void SetRows(
        EcsSnapshot<Transform, Ecs.Components.Renderer> renderers,
        int start,
        int length,
        int lastCount,
        uint frameIndex
    )
    {
        for (int index = start; index < start + length; index++)
        {
            Entity entity = renderers.Entities[index];
            Ecs.Components.Renderer renderer = renderers.Second[index];
            ResidentModel? resident = renderer.Model == Handle<Model>.None ? null : resources.Get(renderer.Model);

            // A resident model's row: where it is, its bounds in the world and the material rows of its slots, every
            // slot the failure's when the model failed.
            GpuInstance row = default;
            if (resident is { } model)
            {
                Transform transform = renderers.First[index];
                Matrix4x4 worldMatrix = transform.Matrix;
                Matrix4x4.Invert(worldMatrix, out Matrix4x4 inverseWorld);
                Vector3 scale = transform.Scale;
                float maxScale = Math.Max(Math.Abs(scale.X), Math.Max(Math.Abs(scale.Y), Math.Abs(scale.Z)));
                row = new GpuInstance
                {
                    World = worldMatrix,
                    InverseWorld = inverseWorld,
                    PreviousWorld = worldMatrix,
                    BoundsCenter = Vector3.Transform(model.BoundsCenter, worldMatrix),
                    BoundsRadius = model.BoundsRadius * maxScale,
                    Model = model.ModelIndex,
                };
                ImmutableArray<MaterialInstance> slots = renderer.Materials.All;
                for (int slot = 0; slot < MaterialSet.Capacity; slot++)
                {
                    MaterialInstance held = slot < slots.Length ? slots[slot] : default;
                    row.Materials[slot] = GetMaterialRow(model.IsFailure ? failure : held);
                }
            }

            bool wasDrawnHere = index < lastCount && _entities[index] == entity && _rows[index].Model != 0;
            if (wasDrawnHere && _rows[index].World != row.World)
            {
                row.PreviousWorld = _rows[index].World;
                row.MovedFrame = frameIndex;
            }

            _rows[index] = row;
            _entities[index] = entity;
        }

        for (int block = start / BlockSize; block <= (start + length - 1) / BlockSize; block++)
        {
            _isDirty[block] = true;
        }

        _version++;
    }

    /// <summary>
    /// The row of <paramref name="material"/>, which failed, in the failure table; the first ask makes it, its text
    /// "Material failed " and the material's path, cut from the left to fit, so the file's name stays.
    /// </summary>
    private uint GetFailureRow(Handle<Material> material)
    {
        if (_failureRowOf.TryGetValue(material.Id, out uint row))
            return row;

        const string Text = "Material failed ";
        string path = assets.GetPath(material) ?? $"material {material.Id}";
        int kept = Math.Min(path.Length, GpuFailureText.Length - Text.Length);
        row = (uint)_failureRowOf.Count + 1;
        if (row == _failureRows.Length)
            Array.Resize(ref _failureRows, _failureRows.Length * 2);

        _failureRows[row] = GpuFailure.Create(FailureKind.Material, FailurePattern.Solid, Text + path[^kept..]);
        _failureRowOf[material.Id] = row;

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

        if (_materialSlots.Count >= MaxCapacity)
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
}

/// <summary>
/// The frame's instances as the instance table holds them on the CPU, for the gems that build from each instance: the
/// rows as the GPU reads them, how many rows the material table holds and has room for, so a table with a row for each
/// of its rows is asked for at that size, and how many times the rows have changed, so what is built from them is built
/// again when it differs. Read on the render thread in the frame it is made for.
/// </summary>
public readonly record struct GpuInstances(
    ReadOnlyMemory<GpuInstance> Rows,
    int MaterialCount,
    int MaterialCapacity,
    long Version
);
