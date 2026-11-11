using System.Numerics;
using Magic.Assets;
using Magic.Debugging;
using Magic.GpuWorld;
using Magic.Graphics;
using Magic.Projects;
using Magic.Utils;

namespace Magic.AccelerationStructures;

/// <summary>
/// What the acceleration structures take each frame: the world as the GPU holds it, and its instances as the CPU does.
/// </summary>
public readonly record struct AccelerationStructuresInput(GpuView View, GpuInstances Instances);

/// <summary>
/// The world as rays see it, on a GPU that traces rays: the top-level acceleration structure over the instances, an
/// instance row for every instance whose model has its triangles' structure, built again when the instances change,
/// what a ray query traces through; and the hit materials table, each material row averaged by a pass of its material
/// shader each frame, what a ray hit reads its surface from. Each frame it writes them into the frame's buffer and makes
/// the frame's <see cref="TracedWorld"/>; on a GPU that doesn't trace rays it makes none. The instances, their models'
/// structures and the material rows are the world's gem's, taken as its <see cref="GpuInstances"/>. It is public
/// because nothing in its assembly makes it: Magic does, by reflection, when a project lists the gem.
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
    /// The average entry for each material shader the world's gem has numbered, in their order.
    /// </summary>
    private readonly List<ShaderEntry> _averageShaders = [];

    /// <summary>
    /// The instance table's version the last build was of, how many instances rays see in it, those whose model has its
    /// triangles' structure, and the box over their world boxes, min over max where none do.
    /// </summary>
    private (long Version, int Count, (Vector3 Min, Vector3 Max) Box) _built;

    /// <summary>
    /// The world as rays see it this frame, built again when the instances changed, written into the frame's buffer;
    /// none on a GPU that doesn't trace rays, or while what it is built into is not made.
    /// </summary>
    public TracedWorld? Render(in Frame frame, RenderGraph graph, in AccelerationStructuresInput input)
    {
        if (!graph.Features.HasRayQuery)
            return null;

        GpuView view = input.View;
        GpuInstances world = input.Instances;

        // The structure over the view's instances and its instances asked for every frame, so the structure built last
        // stays, and the hit materials table with a row for each of the material table's.
        Result<GpuBuffer> hitMaterials = graph.Buffer(
            GpuDescriptions.GetBuffer("Hit materials", (long)world.MaterialCapacity * HitMaterialBytes)
        );
        Result<GpuBuffer> instances = graph.Buffer(
            GpuDescriptions.GetBuffer(
                "Acceleration instances",
                (long)view.InstanceCapacity * GpuAccelerationInstance.Bytes
            )
        );
        Result<GpuAcceleration> structure = instances.Ok
            ? graph.Acceleration(
                new TopLevelDescription
                {
                    MaxInstances = view.InstanceCapacity,
                    Instances = instances.Value,
                    Name = "World acceleration",
                }
            )
            : Result.Failure(instances.Error);
        if (Result.FindFailure(hitMaterials, instances, structure) is { Ok: false } failed)
        {
            Log.Error($"The world's acceleration structure is not made: {failed.Error}");

            return null;
        }

        // Built again whenever the rows changed since the last build; a model's arrival builds every row again.
        if (world.Version != _built.Version)
            _built = Build(graph, instances.Value, structure.Value, world);

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

        GpuAcceleration acceleration = _built.Count == 0 ? GpuAcceleration.None : structure.Value;
        graph.Upload(
            view.Frame,
            [new GpuTracedWorld { Acceleration = acceleration.Id, HitMaterials = hitMaterials.Value.Id }],
            GpuTracedWorld.Offset
        );

        return new TracedWorld(acceleration, hitMaterials.Value, _built.Box);
    }

    /// <summary>
    /// The structure built again over the instances whose model has its triangles' structure, their rows written
    /// straight into the frame's upload ring, in the frame's transfers, after the world's gem uploaded the models'
    /// structures; and the build's version, how many instances it holds and the box over their world boxes, min over
    /// max where none do. None is built for no instance.
    /// </summary>
    private static (long Version, int Count, (Vector3 Min, Vector3 Max) Box) Build(
        RenderGraph graph,
        GpuBuffer instances,
        GpuAcceleration structure,
        GpuInstances world
    )
    {
        // Counted first, since the upload ring is asked for the rows before they are written.
        ReadOnlySpan<InstanceExtra> extras = world.Extras.Span;
        int count = 0;
        (Vector3 Min, Vector3 Max) box = (new Vector3(float.MaxValue), new Vector3(float.MinValue));
        foreach (InstanceExtra extra in extras)
        {
            if (extra.Acceleration == 0)
                continue;

            count++;
            box = (Vector3.Min(box.Min, extra.Box.Min), Vector3.Max(box.Max, extra.Box.Max));
        }

        if (count == 0)
            return (world.Version, count, box);

        ReadOnlySpan<GpuInstance> rows = world.Rows.Span;
        Span<GpuAccelerationInstance> written = graph.Upload<GpuAccelerationInstance>(instances, count);
        int next = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            InstanceExtra extra = extras[i];
            if (extra.Acceleration == 0)
                continue;

            Matrix4x4 transform = rows[i].World;
            written[next++] = new GpuAccelerationInstance
            {
                Row0 = new(transform.M11, transform.M21, transform.M31, transform.M41),
                Row1 = new(transform.M12, transform.M22, transform.M32, transform.M42),
                Row2 = new(transform.M13, transform.M23, transform.M33, transform.M43),
                CustomIndexAndMask = (uint)i | (0xFFu << 24),
                OffsetAndFlags = extra.MeshOffset | (GpuAccelerationInstance.FlagNoOpaque << 24),
                Reference = extra.Acceleration,
            };
        }

        graph.BuildAcceleration(structure, (uint)count);

        return (world.Version, count, box);
    }
}
