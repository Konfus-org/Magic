using System.Linq;
using System.Numerics;
using Magic.Assets;
using Magic.Debugging;
using Magic.DeferredRendering;
using Magic.DeferredRendering.View;
using Magic.DeferredRendering.World;
using Magic.Graphics;
using Magic.Projects;
using Magic.Utils;

namespace Magic.AccelerationStructures;

/// <summary>
/// What the acceleration structures take each frame: the world as the GPU holds it, and its instances as the CPU does.
/// </summary>
public readonly record struct AccelerationStructuresInput(GpuView View, GpuInstances Instances);

/// <summary>
/// The world as rays see it, on a GPU that traces rays: each model's triangles' structure, a geometry for each of its
/// meshes in the mesh table, built once as the model arrives and let go as it leaves; the top-level acceleration
/// structure over the instances, an instance row for every instance whose model has its triangles' structure, built
/// again when the instances or those structures change, what a ray query traces through; and the hit materials table,
/// each material row averaged by a pass of its material shader each frame, what a ray hit reads its surface from. Each
/// frame it writes them into the frame's buffer and makes the frame's <see cref="TracedWorld"/>; on a GPU that doesn't
/// trace rays it makes none. The models, the instances and the material rows are the renderer's, taken as its
/// <see cref="GpuView"/> and <see cref="GpuInstances"/>. It is public because nothing in its assembly makes it: Magic
/// does, by reflection, when a project lists the gem.
/// </summary>
public sealed class AccelerationStructuresGem(AssetManager assets)
    : IGem,
        IRender<AccelerationStructuresInput, TracedWorld>
{
    /// <summary>
    /// A hit materials row, a <c>GpuHitMaterial</c> as the shader library lays it out.
    /// </summary>
    private const int HitMaterialBytes = 32;

    private readonly ShaderEntry _averageShader = PassShaders.GetEntry(assets, "AverageMaterials", "AverageMaterials");

    /// <summary>
    /// The average entry for each material shader the renderer has numbered, in their order.
    /// </summary>
    private readonly List<ShaderEntry> _averageShaders = [];

    /// <summary>
    /// Each model's triangles' structure, by its model index: the place it is made for, what it is made to, none when
    /// the model has no meshes or it could not be made, and the structure, none until it is made.
    /// </summary>
    private readonly List<(ModelPlace Place, BottomLevelDescription? Description, GpuAcceleration Structure)> _models =
    [];

    /// <summary>
    /// The instance table's version the last build was of, how many instances rays see in it, those whose model has its
    /// triangles' structure, and the box over their world boxes, min over max where none do.
    /// </summary>
    private (long Version, int Count, (Vector3 Min, Vector3 Max) Box) _built;

    /// <summary>
    /// The world as rays see it this frame, built again when the instances or the models' structures changed, written
    /// into the frame's buffer; none on a GPU that doesn't trace rays, or while what it is built into is not made.
    /// </summary>
    public TracedWorld? Render(in Frame frame, RenderGraph graph, in AccelerationStructuresInput input)
    {
        if (!graph.Features.HasRayQuery)
            return null;

        GpuView view = input.View;
        GpuInstances world = input.Instances;

        // Each model's structure asked for every frame, so the GPU provider keeps it, row for row with the view's
        // models. A row whose model changed lets the old structure go, and has the new model's made and built over its
        // meshes in the pools; one the provider compacted comes back moved, holding what it held. Either builds the
        // instances again. A row past the view's last row in use is dropped, and its structure, no longer asked for,
        // goes.
        bool isChanged = world.Version != _built.Version;
        ReadOnlySpan<ModelPlace> models = view.Models.Span;
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
                                view.Vertices,
                                0,
                                GpuVertex.Bytes,
                                place.VertexOffset + place.VertexCount,
                                view.Indices,
                                (ulong)mesh.Mesh.IndexOffset * sizeof(uint),
                                mesh.Triangles
                            )),
                        ],
                        Name =
                            $"{assets.GetPath(place.Model) ?? $"model {place.Model.Id}"} acceleration {place.VertexOffset}",
                    };
                }
            }

            if (row.Description is { } description)
            {
                Result<GpuAcceleration> structure = graph.Acceleration(description);
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

        // The structure over the view's instances and its instances asked for every frame, so the structure built last
        // stays, and the hit materials table with a row for each of the material table's.
        Result<GpuBuffer> hitMaterials = graph.Buffer(
            new("Hit materials", (long)world.MaterialCapacity * HitMaterialBytes)
        );
        Result<GpuBuffer> instances = graph.Buffer(
            new("Acceleration instances", (long)view.InstanceCapacity * GpuAccelerationInstance.Bytes)
        );
        Result<GpuAcceleration> topLevel = instances.Ok
            ? graph.Acceleration(
                new TopLevelDescription
                {
                    MaxInstances = view.InstanceCapacity,
                    Instances = instances.Value,
                    Name = "World acceleration",
                }
            )
            : Result.Failure(instances.Error);
        if (Result.FindFailure(hitMaterials, instances, topLevel) is { Ok: false } failed)
        {
            Log.Error($"The world's acceleration structure is not made: {failed.Error}");

            return null;
        }

        // Built again over the instances whose model has its structure, their rows written straight into the frame's
        // upload ring after the models' structures are built; counted first, since the ring is asked for the rows
        // before they are written. None is built for no instance.
        if (isChanged)
        {
            ReadOnlySpan<GpuInstance> rows = world.Rows.Span;
            int count = 0;
            (Vector3 Min, Vector3 Max) box = (new Vector3(float.MaxValue), new Vector3(float.MinValue));
            foreach (GpuInstance instance in rows)
            {
                (ModelPlace place, _, GpuAcceleration structure) = _models[(int)instance.Model];
                if (structure.IsNone)
                    continue;

                (Vector3 min, Vector3 max) = ToWorldBox(place.BoundsMin, place.BoundsMax, instance.World);
                count++;
                box = (Vector3.Min(box.Min, min), Vector3.Max(box.Max, max));
            }

            if (count > 0)
            {
                Span<GpuAccelerationInstance> written = graph.Upload<GpuAccelerationInstance>(instances.Value, count);
                int next = 0;
                for (int i = 0; i < rows.Length; i++)
                {
                    (ModelPlace place, _, GpuAcceleration structure) = _models[(int)rows[i].Model];
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

            _built = (world.Version, count, box);
        }

        // Each material row averaged into the hit materials, a pass for each material shader, which fills the rows it
        // draws.
        for (int shader = _averageShaders.Count; shader < view.MaterialShaders.Count; shader++)
        {
            _averageShaders.Add(_averageShader with { TypeArguments = [view.MaterialShaders[shader]] });
        }

        for (int shader = 0; shader < _averageShaders.Count; shader++)
        {
            graph
                .Compute(
                    "Average materials",
                    _averageShaders[shader],
                    new GpuConstants(view.Frame.Id, hitMaterials.Value.Id, (uint)shader, (uint)world.MaterialCount),
                    RenderStage.Prepare
                )
                .Reads(view.Frame, view.Materials)
                .Reads(view.HeapTextures.AsSpan())
                .Writes(hitMaterials.Value)
                .DispatchOver(world.MaterialCount, 1);
        }

        GpuAcceleration acceleration = _built.Count == 0 ? GpuAcceleration.None : topLevel.Value;
        graph.Upload(
            view.Frame,
            [new GpuTracedWorld { Acceleration = acceleration.Id, HitMaterials = hitMaterials.Value.Id }],
            GpuTracedWorld.Offset
        );

        return new TracedWorld(acceleration, hitMaterials.Value, _built.Box);
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
}
