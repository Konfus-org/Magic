using System.Linq;
using System.Numerics;
using Magic.Assets;
using Magic.Debugging;
using Magic.DeferredRendering;
using Magic.DeferredRendering.View;
using Magic.DeferredRendering.World;
using Magic.Ecs.Components;
using Magic.Graphics;
using Magic.Utils;

namespace Magic.AccelerationStructures;

/// <summary>
/// What the acceleration structures take each frame: the world as the GPU holds it, and its instances as the CPU does.
/// </summary>
public readonly record struct AccelerationStructuresInput(GpuWorld World, GpuInstances Instances);

/// <summary>
/// Builds the world as rays see it each frame, on a GPU that traces rays, and makes the frame's
/// <see cref="TracedWorld"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each model's triangles' structure has a geometry for each of its meshes in the mesh table. It is built once as the
/// model arrives and let go as it leaves.
/// </para>
/// <para>
/// An instance whose renderer leaves slots empty traces through a structure of its model with no triangles in the
/// meshes of those slots, kept while an instance draws with it.
/// </para>
/// <para>
/// The top-level acceleration structure, what a ray query traces through, has an instance row for every instance whose
/// model has its triangles' structure. It is built again when the instances or those structures change.
/// </para>
/// <para>
/// The hit materials table, what a ray hit reads its surface from, has each material row averaged by a pass of its
/// material shader each frame.
/// </para>
/// <para>
/// The renderer writes the frame's <see cref="TracedWorld"/> into each view's buffer; on a GPU that doesn't trace rays
/// it makes none. The models, the instances and the material rows are the renderer's, taken as its
/// <see cref="GpuWorld"/> and <see cref="GpuInstances"/>.
/// </para>
/// </remarks>
public sealed class Renderer : IRender<AccelerationStructuresInput, TracedWorld>
{
    /// <summary>
    /// A hit materials row, a <c>GpuHitMaterial</c> as the renderer's <c>Trace/HitSurface.slang</c> lays it out.
    /// </summary>
    private const int HitMaterialBytes = 32;

    private readonly AssetManager _assets;

    private readonly IGraphicsApi _gpu;

    private readonly ShaderEntry _averageShader;

    /// <summary>
    /// The average entry for each material shader the renderer has numbered, in their order.
    /// </summary>
    private readonly List<ShaderEntry> _averageShaders = [];

    /// <summary>
    /// Each model's triangles' structure, by its model index.
    /// </summary>
    /// <remarks>
    /// Each holds the place it is made for, what it is made to, none when the model has no meshes or it could not be
    /// made, and the structure, none until it is made.
    /// </remarks>
    private readonly List<(ModelPlace Place, BottomLevelDescription? Description, GpuAcceleration Structure)> _models =
    [];

    /// <summary>
    /// Each structure of a model with empty slots that an instance draws with.
    /// </summary>
    private List<EmptySlotsStructure> _withEmptySlots = [];

    /// <summary>
    /// The instance table's version the last build was of, how many instances rays see in it, and the box over them.
    /// </summary>
    /// <remarks>
    /// Rays see the instances whose model has its triangles' structure. The box is over their world boxes, min over max
    /// where none do.
    /// </remarks>
    private (long Version, int Count, (Vector3 Min, Vector3 Max) Box) _built;

    internal Renderer(AssetManager assets, IGraphicsApi gpu)
    {
        _assets = assets;
        _gpu = gpu;
        _averageShader = PassShaders.GetEntry(assets, "AverageMaterials", "AverageMaterials");
    }

    /// <summary>
    /// Builds the world as rays see it this frame, again when the instances or the models' structures changed.
    /// </summary>
    /// <returns>
    /// The world as rays see it; none on a GPU that doesn't trace rays, or while what it is built into is not made.
    /// </returns>
    public TracedWorld? Render(in RenderFrame frame, RenderGraph graph, in AccelerationStructuresInput input)
    {
        if (!_gpu.Features.HasRayQuery)
            return null;

        GpuWorld world = input.World;
        GpuInstances drawn = input.Instances;

        // Each model's structure asked for every frame, so the GPU provider keeps it, row for row with the world's
        // models. A row whose model changed lets the old structure go, and has the new model's made and built over its
        // meshes in the pools; one the provider compacted comes back moved, holding what it held. Either builds the
        // instances again. A row past the world's last row in use is dropped, and its structure, no longer asked for,
        // goes.
        bool isChanged = drawn.Version != _built.Version;
        ReadOnlySpan<ModelPlace> models = world.Models.Span;
        if (_models.Count > models.Length)
            _models.RemoveRange(models.Length, _models.Count - models.Length);

        while (_models.Count < models.Length)
        {
            _models.Add(default);
        }

        for (int index = 0; index < models.Length; index++)
        {
            (ModelPlace Place, BottomLevelDescription? Description, GpuAcceleration Structure) row = _models[index];
            ModelPlace place = models[index];
            if (row.Place != place)
            {
                // The indices hold the vertex offset, so the name does too: one name and description are one set of
                // triangles.
                row = (place, null, GpuAcceleration.None);
                if (!place.Meshes.IsDefaultOrEmpty)
                {
                    row.Description = new BottomLevelDescription
                    {
                        Geometries =
                        [
                            .. place.Meshes.Select(mesh => new TriangleGeometry(
                                world.Vertices,
                                0,
                                GpuVertex.Bytes,
                                place.VertexOffset + place.VertexCount,
                                world.Indices,
                                (ulong)mesh.Mesh.IndexOffset * sizeof(uint),
                                mesh.Triangles
                            )),
                        ],
                        Name =
                            $"{_assets.GetPath(place.Model) ?? $"model {place.Model.Id}"} acceleration {place.VertexOffset}",
                    };
                }
            }

            if (row.Description is { } description)
            {
                Result<GpuAcceleration> structure = _gpu.GetAcceleration(description);
                if (!structure.Ok && row.Structure.IsNone)
                {
                    Log.Warn($"{description.Name} is not made, so rays pass through its model: {structure.Error}");
                    row.Description = null;
                }
                else if (structure.Ok && structure.Value != row.Structure)
                {
                    if (row.Structure.IsNone)
                        graph.BuildAcceleration(structure.Value);

                    row.Structure = structure.Value;
                    isChanged = true;
                }
            }

            _models[index] = row;
        }

        // A structure for each model and empty slots that an instance draws with, made again when the instances or
        // the models changed; one of an instance whose every mesh is empty is not made, so rays pass it all.
        if (isChanged)
        {
            List<EmptySlotsStructure> kept = [];
            foreach (GpuInstance instance in drawn.Rows.Span)
            {
                (ModelPlace place, BottomLevelDescription? description, _) = _models[(int)instance.Model];
                uint empty = GetEmptySlots(instance, place);
                if (
                    description is not { } whole
                    || empty == 0
                    || place.Meshes.All(mesh => IsEmpty(empty, mesh.Mesh))
                    || kept.Exists(held => held.Model == instance.Model && held.EmptySlots == empty)
                )
                    continue;

                int index = _withEmptySlots.FindIndex(held =>
                    held.Model == instance.Model && held.EmptySlots == empty && held.Place == place
                );
                kept.Add(
                    index >= 0
                        ? _withEmptySlots[index]
                        : new EmptySlotsStructure(
                            instance.Model,
                            empty,
                            place,
                            whole with
                            {
                                Geometries =
                                [
                                    .. whole.Geometries.Select(
                                        (geometry, mesh) =>
                                            IsEmpty(empty, place.Meshes[mesh].Mesh)
                                                ? geometry with
                                                {
                                                    TriangleCount = 0,
                                                }
                                                : geometry
                                    ),
                                ],
                                Name = $"{whole.Name} without slots {empty:b}",
                            },
                            GpuAcceleration.None
                        )
                );
            }

            _withEmptySlots = kept;
        }

        for (int index = 0; index < _withEmptySlots.Count; index++)
        {
            EmptySlotsStructure held = _withEmptySlots[index];
            Result<GpuAcceleration> structure = _gpu.GetAcceleration(held.Description);
            if (!structure.Ok || structure.Value == held.Structure)
                continue;

            if (held.Structure.IsNone)
                graph.BuildAcceleration(structure.Value);

            _withEmptySlots[index] = held with { Structure = structure.Value };
            isChanged = true;
        }

        // The structure over the world's instances and its instances asked for every frame, so the structure built last
        // stays, and the hit materials table with a row for each of the material table's.
        Result<GpuBuffer> hitMaterials = _gpu.GetBuffer(
            new("Hit materials", (long)drawn.MaterialCapacity * HitMaterialBytes)
        );
        Result<GpuBuffer> instances = _gpu.GetBuffer(
            new("Acceleration instances", (long)world.InstanceCapacity * GpuAccelerationInstance.Bytes)
        );
        Result<GpuAcceleration> topLevel = instances.Ok
            ? _gpu.GetAcceleration(
                new TopLevelDescription
                {
                    MaxInstances = world.InstanceCapacity,
                    Instances = instances.Value,
                    Name = "World acceleration",
                }
            )
            : Result.Failure(instances.Error);
        if (Result.FindFailure(hitMaterials, instances, topLevel) is { Ok: false } failed)
        {
            frame.Fail(RenderFailure.Resource, "Ray structure is not made", failed.Error);

            return null;
        }

        // Built again over the instances whose model has its structure, their rows written straight into the frame's
        // upload ring after the models' structures are built; counted first, since the ring is asked for the rows
        // before they are written. None is built for no instance.
        if (isChanged)
        {
            ReadOnlySpan<GpuInstance> rows = drawn.Rows.Span;
            int count = 0;
            (Vector3 Min, Vector3 Max) box = (new Vector3(float.MaxValue), new Vector3(float.MinValue));
            foreach (GpuInstance instance in rows)
            {
                (ModelPlace place, GpuAcceleration structure) = GetStructure(instance);
                if (structure.IsNone)
                    continue;

                (Vector3 min, Vector3 max) = ToWorldBox(place.BoundsMin, place.BoundsMax, instance.World);
                count++;
                box = (Vector3.Min(box.Min, min), Vector3.Max(box.Max, max));
            }

            if (count > 0)
            {
                Span<GpuAccelerationInstance> written = graph.Upload<GpuAccelerationInstance>(instances.Value, count);

                // Rows not staged: built for no version, so a later frame builds it, and no ray is traced in this one.
                if (written.IsEmpty)
                {
                    _built = (-1, 0, default);

                    return null;
                }
                int next = 0;
                for (int i = 0; i < rows.Length; i++)
                {
                    (ModelPlace place, GpuAcceleration structure) = GetStructure(rows[i]);
                    if (structure.IsNone)
                        continue;

                    Matrix4x4 transform = rows[i].World;
                    written[next++] = new GpuAccelerationInstance
                    {
                        Row0 = new(transform.M11, transform.M21, transform.M31, transform.M41),
                        Row1 = new(transform.M12, transform.M22, transform.M32, transform.M42),
                        Row2 = new(transform.M13, transform.M23, transform.M33, transform.M43),
                        CustomIndexAndMask = (uint)i | (0xFFu << 24),
                        OffsetAndFlags = place.MeshOffset | (GpuAccelerationInstance.FlagNoOpaque << 24),
                        Reference = structure.Address,
                    };
                }

                graph.BuildAcceleration(topLevel.Value, (uint)count);
            }

            _built = (drawn.Version, count, box);
        }

        // Each material row averaged into the hit materials, a pass for each material shader, which fills the rows it
        // draws.
        for (int shader = _averageShaders.Count; shader < world.MaterialShaders.Count; shader++)
        {
            _averageShaders.Add(_averageShader with { TypeArguments = [world.MaterialShaders[shader]] });
        }

        for (int shader = 0; shader < _averageShaders.Count; shader++)
        {
            graph
                .Compute(
                    "Average materials",
                    _averageShaders[shader],
                    new GpuConstants(world.Frame.Id, hitMaterials.Value.Id, (uint)shader, (uint)drawn.MaterialCount),
                    RenderStage.Prepare
                )
                .Reads(world.Frame, world.Materials)
                .Reads(world.HeapTextures.AsSpan())
                .Writes(hitMaterials.Value)
                .DispatchOver(drawn.MaterialCount, 1);
        }

        GpuAcceleration acceleration = _built.Count == 0 ? GpuAcceleration.None : topLevel.Value;

        return new TracedWorld(acceleration, hitMaterials.Value, _built.Box);
    }

    /// <summary>
    /// Gets the slots, a bit each, that the instance leaves empty and a mesh of its model is in.
    /// </summary>
    private static uint GetEmptySlots(GpuInstance instance, ModelPlace place)
    {
        uint empty = 0;
        foreach ((GpuMesh mesh, _) in place.Meshes)
        {
            if (instance.Materials[GetSlot(mesh)] == GpuMaterialRows.EmptySlot)
                empty |= 1u << GetSlot(mesh);
        }

        return empty;
    }

    /// <summary>
    /// Gets the mesh's slot as the instance table reads it: the last for one past it.
    /// </summary>
    private static int GetSlot(GpuMesh mesh) => (int)Math.Min(mesh.MaterialSlot, MaterialSlots.Capacity - 1);

    private static bool IsEmpty(uint emptySlots, GpuMesh mesh) => (emptySlots & (1u << GetSlot(mesh))) != 0;

    /// <summary>
    /// Gets the box in the world around a box in a model's frame placed by <paramref name="world"/>.
    /// </summary>
    /// <remarks>
    /// Its centre is placed, and its half extent along each world axis is the sum of how far each of the model's axes
    /// reaches along it.
    /// </remarks>
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
    /// Gets the instance's model's place, and the structure rays trace it through: its model's, or the one of its model
    /// with its empty slots; none while that is not made, or when every mesh is empty.
    /// </summary>
    private (ModelPlace Place, GpuAcceleration Structure) GetStructure(GpuInstance instance)
    {
        (ModelPlace place, _, GpuAcceleration structure) = _models[(int)instance.Model];
        uint empty = GetEmptySlots(instance, place);
        if (empty == 0)
            return (place, structure);

        int index = _withEmptySlots.FindIndex(held => held.Model == instance.Model && held.EmptySlots == empty);

        return (place, index >= 0 ? _withEmptySlots[index].Structure : GpuAcceleration.None);
    }

    /// <summary>
    /// A structure of a model with empty slots, a bit each: its model's geometries, those in an empty slot with no
    /// triangles, so rays pass them and each geometry keeps its index.
    /// </summary>
    /// <remarks>
    /// It holds the place it is made for, what it is made to, and the structure, none until it is made.
    /// </remarks>
    private readonly record struct EmptySlotsStructure(
        uint Model,
        uint EmptySlots,
        ModelPlace Place,
        BottomLevelDescription Description,
        GpuAcceleration Structure
    );
}
