using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using Magic.Assets;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Graphics;
using Magic.Utils;

namespace Magic.DeferredRendering.World;

/// <summary>
/// The instance table, a row per renderer at its index in a snapshot of the renderers, and the material table beside
/// it.
/// </summary>
/// <remarks>
/// <para>
/// A frame's work is the chunks of renderers written since the snapshot the last fill read, or that moved: their rows
/// are built again, each renderer asking for its model, and the rest stay as they are. Every row is built again once a
/// model is made or fails. Blocks of rows that changed are uploaded in runs; a still scene uploads none.
/// </para>
/// <para>
/// The material table has a row per distinct <see cref="MaterialInstance"/> drawn, which every slot holding it shares,
/// made again each frame from what <paramref name="resources"/> has then, and uploaded whole. Row 0 is the default
/// material, which an empty slot and a material still loading are drawn with.
/// </para>
/// <para>
/// A material that failed, or whose shader did, is drawn with row 0's params and shader, and the index of its row in
/// the failure table, which names it. A material with a texture that failed, or a param that fits no field, is drawn as
/// itself with that failure's index. A model that failed is drawn as the failure cube, every slot with row 0's params
/// and shader and the index of the model's row in the failure table.
/// </para>
/// </remarks>
internal sealed class InstanceTable(AssetManager assets, GpuResources resources)
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
    /// The material rows, a row for each distinct slot drawn, up to as many as there are instance rows.
    /// </summary>
    /// <remarks>
    /// That holds every drawn slot of a world whose renderers fill one each.
    /// </remarks>
    private GpuMaterial[] _materialRows = new GpuMaterial[MinCapacity];

    /// <summary>
    /// The slot each material row is made from, by row, with the id of the failed model whose cube it draws, 0 for
    /// none; row 0 the default material's.
    /// </summary>
    private readonly List<(MaterialInstance Slot, ulong FailedModel)> _materialSlots =
    [
        (new() { Handle = assets.GetDefault<Material>() }, 0),
    ];

    private readonly Dictionary<(MaterialInstance Slot, ulong FailedModel), uint> _materialRowOf = [];

    /// <summary>
    /// The failure table's rows, a row for each failure drawn, made when it is first drawn and freed when its asset is
    /// read again.
    /// </summary>
    /// <remarks>
    /// Row 0 is never read, as a failure index of 0 means no failure.
    /// </remarks>
    private GpuFailure[] _failureRows = new GpuFailure[16];

    /// <summary>
    /// The row of each failure drawn in the failure table, by its kind, the asset that failed and the param that fits
    /// no field.
    /// </summary>
    private readonly Dictionary<(FailureKind Kind, ulong Asset, string? Param), uint> _failureRowOf = [];

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
    /// The frame's instances: the rows, how many rows the material table holds and has room for, and how many times the
    /// rows have changed.
    /// </summary>
    public GpuInstances Instances =>
        new(_rows.AsMemory(0, Count), _materialSlots.Count, _materialRows.Length, _version);

    /// <summary>
    /// Frees the failure table's rows of asset <paramref name="id"/>, read again, so a failure of it still there is
    /// made, and logged, anew.
    /// </summary>
    /// <remarks>
    /// The rows after them move down, as each frame's material rows name them again.
    /// </remarks>
    public void Forget(ulong id)
    {
        if (!_failureRowOf.Keys.Any(failure => failure.Asset == id))
            return;

        KeyValuePair<(FailureKind Kind, ulong Asset, string? Param), uint>[] kept =
        [
            .. _failureRowOf.Where(failure => failure.Key.Asset != id).OrderBy(failure => failure.Value),
        ];
        _failureRowOf.Clear();
        foreach (((FailureKind Kind, ulong Asset, string? Param) failure, uint old) in kept)
        {
            uint row = (uint)_failureRowOf.Count + 1;
            _failureRows[row] = _failureRows[old];
            _failureRowOf[failure] = row;
        }
    }

    /// <summary>
    /// Builds the rows of <paramref name="renderers"/> from what changed since the last fill.
    /// </summary>
    /// <remarks>
    /// Each row holds its model's place in the pools and the material rows of its slots, or is empty while its model is
    /// being made.
    /// </remarks>
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
    /// Asks for the table, the material table and the failure table, and copies what changed into them, in the frame's
    /// upload pass.
    /// </summary>
    /// <remarks>
    /// The blocks of rows that changed go into the table, each run of them in one copy. The material rows are made
    /// again from what the GPU resources have now and copied into theirs, and the failure rows are copied into theirs.
    /// </remarks>
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

        // Every material row made again, so a material or texture that arrived shows with no renderer written: one
        // still loading as the default, row 0; one that failed, or its shader did, as the default with its failure; one
        // that a model that failed is drawn with, the default, with the model's; and one with a part that failed, a
        // texture or a param, as itself with that part's failure.
        for (int row = 0; row < _materialSlots.Count; row++)
        {
            (MaterialInstance slot, ulong failedModel) = _materialSlots[row];
            GpuState state = resources.Get(
                slot,
                out _materialRows[row],
                out (FailureKind Kind, ulong Asset, string? Param)? failure
            );

            // A failed model's slot shows no failure once the model is read again.
            uint failed =
                failedModel != 0
                    ? resources.HasFailed(new Handle<Model>(failedModel))
                        ? GetFailureRow((FailureKind.Model, failedModel, null))
                        : 0
                    : failure is { } part
                        ? GetFailureRow(part)
                        : 0;

            if (state != GpuState.Ready)
                _materialRows[row] = _materialRows[0];

            if (failed != 0)
            {
                _materialRows[row].Shader =
                    (_materialRows[row].Shader & ~(GpuMaterial.FailureMask << GpuMaterial.FailureShift))
                    | (failed << GpuMaterial.FailureShift);
            }
        }

        if (!graph.Upload(materials.Value, _materialRows.AsSpan(0, _materialSlots.Count)))
            return Result.Failure("the material table was not staged.");

        // The failure table, asked for after the material rows made its rows, so each row they name is in it.
        Result<GpuBuffer> failures = graph.Buffer(new("Failure table", (long)_failureRows.Length * GpuFailure.Bytes));
        if (!failures.Ok)
            return Result.Failure(failures.Error);

        if (!graph.Upload(failures.Value, _failureRows.AsSpan(0, _failureRowOf.Count + 1)))
            return Result.Failure("the failure table was not staged.");

        // The blocks of rows that changed, each run of them in one copy; a run not staged stays changed for the next
        // frame, and nothing draws from the table in this one.
        bool isSent = true;
        int blocks = (Count + BlockSize - 1) / BlockSize;
        int block = 0;
        while (block < blocks)
        {
            if (!_isDirty[block])
            {
                block++;
                continue;
            }

            int runBlock = block;
            int runStart = block * BlockSize;
            int runRows = 0;
            for (; block < blocks && _isDirty[block]; block++)
            {
                runRows += Math.Min(BlockSize, Count - (block * BlockSize));
            }

            if (graph.Upload(rows.Value, _rows.AsSpan(runStart, runRows), (ulong)runStart * GpuInstance.Bytes))
                Array.Clear(_isDirty, runBlock, block - runBlock);
            else
                isSent = false;
        }

        return isSent
            ? Result.Success((rows.Value, materials.Value, failures.Value))
            : Result.Failure("the instance table was not staged.");
    }

    /// <summary>
    /// Gets how many rows a table holds to hold <paramref name="rows"/>: the next power of two, from
    /// <see cref="MinCapacity"/> up to <see cref="MaxCapacity"/>.
    /// </summary>
    private static int GetCapacity(int rows) =>
        (int)Math.Min(BitOperations.RoundUpToPowerOf2((uint)Math.Max(rows, MinCapacity)), MaxCapacity);

    /// <summary>
    /// Builds the rows from <paramref name="start"/> again from their renderers, each asking for its model.
    /// </summary>
    /// <remarks>
    /// A renderer drawn at the same index in the last fill that moved in the world keeps where it was, for this frame's
    /// motion.
    /// </remarks>
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
            // slot the default with the model's failure when the model failed.
            GpuInstance row = default;
            if (resident is { } model)
            {
                // The failure cube keeps its size, whatever the entity's scale, so it shows; it keeps the place and
                // turn.
                Transform transform = renderers.First[index];
                if (model.IsFailure)
                    transform.Scale = Vector3.One;

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
                    row.Materials[slot] = GetMaterialRow(
                        model.IsFailure ? (_materialSlots[0].Slot, renderer.Model.Id) : (held, 0)
                    );
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
    /// Gets the row of <paramref name="failure"/> in the failure table, and makes it on the first ask.
    /// </summary>
    /// <remarks>
    /// The failure is the asset of its kind that failed, and for a param that fits no field of its material's shader,
    /// the param's name. The row is drawn in stripes when a part failed, a texture or a param, else solid. Its text is
    /// the kind, " failed ", the asset's path and the param's name, cut from the left to fit, so the name stays. A
    /// param's first ask logs it too; each other kind is logged where it fails, with why.
    /// </remarks>
    private uint GetFailureRow((FailureKind Kind, ulong Asset, string? Param) failure)
    {
        if (_failureRowOf.TryGetValue(failure, out uint row))
            return row;

        (FailureKind kind, ulong id, string? param) = failure;
        string path =
            kind switch
            {
                FailureKind.Model => assets.GetPath(new Handle<Model>(id)),
                FailureKind.Shader => assets.GetPath(new Handle<Shader>(id)),
                FailureKind.Texture => assets.GetPath(new Handle<Texture>(id)),
                FailureKind.Material or FailureKind.Param => assets.GetPath(new Handle<Material>(id)),
                _ => null,
            } ?? $"id {id}";
        if (param is not null)
        {
            Log.Error(
                $"Material {path} draws {param} as its shader's default: the shader has no field of that name, or of its type."
            );
            path += $" {param}";
        }

        string text = $"{kind} failed ";
        int kept = Math.Min(path.Length, GpuFailureText.Length - text.Length);
        row = (uint)_failureRowOf.Count + 1;
        if (row == _failureRows.Length)
            Array.Resize(ref _failureRows, _failureRows.Length * 2);

        _failureRows[row] = GpuFailure.Create(kind, text + path[^kept..]);
        _failureRowOf[failure] = row;

        return row;
    }

    /// <summary>
    /// Gets the material row of the slot, drawn for the failed model <paramref name="drawn"/>.FailedModel when not 0; 0
    /// for an empty slot.
    /// </summary>
    /// <remarks>
    /// A value first seen takes the next row, which every slot holding it shares. A slot whose params change takes
    /// another row; rows are never given back.
    /// </remarks>
    private uint GetMaterialRow((MaterialInstance Slot, ulong FailedModel) drawn)
    {
        // A failed model's slot takes its own row even with no default material, so it shows the model's failure.
        if (drawn.Slot.Handle == Handle<Material>.None && drawn.FailedModel == 0)
            return 0;

        if (_materialRowOf.TryGetValue(drawn, out uint row))
            return row;

        if (_materialSlots.Count >= MaxCapacity)
        {
            Log.Error(
                $"{assets.GetPath(drawn.Slot.Handle)} is drawn as the default material: the material table is full."
            );
            _materialRowOf[drawn] = 0;

            return 0;
        }

        row = (uint)_materialSlots.Count;
        _materialSlots.Add(drawn);
        _materialRowOf[drawn] = row;

        return row;
    }
}

/// <summary>
/// The frame's instances as the instance table holds them on the CPU.
/// </summary>
/// <remarks>
/// It holds the rows as the GPU reads them; how many rows the material table holds and has room for, so a table with a
/// row for each of its rows is asked for at that size; and how many times the rows have changed, so what is built from
/// them is built again when it differs. Read it on the render thread in the frame it is made for.
/// </remarks>
public readonly record struct GpuInstances(
    ReadOnlyMemory<GpuInstance> Rows,
    int MaterialCount,
    int MaterialCapacity,
    long Version
);
