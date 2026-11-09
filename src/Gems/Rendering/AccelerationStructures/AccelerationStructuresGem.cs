using System.Numerics;
using Magic.Assets;
using Magic.Debugging;
using Magic.GpuWorld;
using Magic.Graphics;
using Magic.Projects;
using Magic.Utils;

namespace Magic.AccelerationStructures;

/// <summary>
/// What the acceleration structures take each frame: the world as the GPU holds it.
/// </summary>
public readonly record struct AccelerationStructuresInput(GpuView View);

/// <summary>
/// The world as rays see it, on a GPU that traces rays: the top-level acceleration structure over the instances, an
/// instance row for every instance whose model has its triangles' structure, built again when the instances change,
/// what a ray query traces through; and the hit materials table, each material row averaged by a pass of its material
/// shader each frame, what a ray hit reads its surface from. Each frame it writes them into the frame's buffer and makes
/// the frame's <see cref="TracedWorld"/>; on a GPU that doesn't trace rays it makes none. The instances, their models'
/// structures and the material rows are the world's gem's. It is public because nothing in its assembly makes it:
/// Magic does, by reflection, when a project lists the gem.
/// </summary>
public sealed class AccelerationStructuresGem(AssetManager assets, GpuWorldGem world)
    : IGem,
        IRender<AccelerationStructuresInput, TracedWorld>
{
    /// <summary>
    /// The twin of <c>AverageMaterials.slang</c>'s group size, a material row each thread.
    /// </summary>
    private const int AverageGroupSize = 64;

    private readonly GpuAccelerationInstance[] _rows = new GpuAccelerationInstance[InstanceTable.Capacity];
    private readonly ShaderEntry _averageShader = new()
    {
        Module = assets.Find<Shader>("Shaders/AverageMaterials.slang"),
        Name = "AverageMaterials",
        Stage = ShaderStage.Compute,
    };

    /// <summary>
    /// The average entry for each material shader the world's gem has numbered, in their order.
    /// </summary>
    private readonly List<ShaderEntry> _averageShaders = [];

    /// <summary>
    /// The structure the last build left, what the frame traces through; none while it holds no instance.
    /// </summary>
    private GpuAcceleration _acceleration;

    /// <summary>
    /// The instance table's version the last build was of.
    /// </summary>
    private long _builtVersion;

    /// <summary>
    /// The world as rays see it this frame, built again when the instances changed, written into the frame's buffer;
    /// none on a GPU that doesn't trace rays, or while what it is built into is not made.
    /// </summary>
    public TracedWorld? Render(in Frame frame, RenderGraph graph, in AccelerationStructuresInput input)
    {
        if (!graph.Features.HasRayQuery)
            return null;

        GpuView view = input.View;
        Result<(GpuBuffer Instances, GpuAcceleration Structure, GpuBuffer HitMaterials)> made = Prepare(graph);
        if (!made.Ok)
        {
            Log.Error($"The world's acceleration structure is not made: {made.Error}");

            return null;
        }

        // Built again whenever the rows changed since the last build; a model's arrival builds every row again.
        (GpuBuffer instances, GpuAcceleration structure, GpuBuffer hitMaterials) = made.Value;
        if (world.Instances.Version != _builtVersion)
        {
            Build(graph, instances, structure, world.Instances.Rows, world.Instances.Extras);
            _builtVersion = world.Instances.Version;
        }

        RecordHitMaterials(graph, view, hitMaterials, world.Instances.MaterialCount);
        (Vector3 boxMin, Vector3 boxMax) = GetBox(world.Instances.Extras);
        GpuTracedWorld traced = new()
        {
            Acceleration = _acceleration.Id,
            HitMaterials = hitMaterials.Id,
            BoxMin = boxMin,
            BoxMax = boxMax,
        };
        graph.Upload(view.Frame, [traced], GpuTracedWorld.Offset);

        return new TracedWorld(_acceleration, hitMaterials);
    }

    /// <summary>
    /// The structure and its instances asked for, every frame, so the structure built last stays, and the hit materials
    /// table.
    /// </summary>
    private static Result<(GpuBuffer Instances, GpuAcceleration Structure, GpuBuffer HitMaterials)> Prepare(
        RenderGraph graph
    )
    {
        Result<GpuBuffer> hitMaterials = graph.Buffer(SharedResources.HitMaterials);
        if (!hitMaterials.Ok)
            return Result.Failure(hitMaterials.Error);

        Result<GpuBuffer> instances = graph.Buffer(
            GpuDescriptions.GetBuffer(
                "Acceleration instances",
                (long)InstanceTable.Capacity * GpuAccelerationInstance.Bytes
            )
        );
        if (!instances.Ok)
            return Result.Failure(instances.Error);

        Result<GpuAcceleration> acceleration = graph.Acceleration(
            new TopLevelDescription
            {
                MaxInstances = InstanceTable.Capacity,
                Instances = instances.Value,
                Name = "World acceleration",
            }
        );
        if (!acceleration.Ok)
            return Result.Failure(acceleration.Error);

        return Result.Success((instances.Value, acceleration.Value, hitMaterials.Value));
    }

    /// <summary>
    /// The box over the world boxes of the instances that have an acceleration structure; min over max where none do.
    /// </summary>
    private static (Vector3 Min, Vector3 Max) GetBox(ReadOnlySpan<InstanceExtra> extras)
    {
        (Vector3 Min, Vector3 Max) box = (new Vector3(float.MaxValue), new Vector3(float.MinValue));
        foreach (InstanceExtra extra in extras)
        {
            if (extra.Acceleration != 0)
                box = (Vector3.Min(box.Min, extra.Box.Min), Vector3.Max(box.Max, extra.Box.Max));
        }

        return box;
    }

    /// <summary>
    /// The structure built again over the instances, in the frame's transfers, after the world's gem uploaded the
    /// models' structures.
    /// </summary>
    private void Build(
        RenderGraph graph,
        GpuBuffer instances,
        GpuAcceleration structure,
        ReadOnlySpan<GpuInstance> rows,
        ReadOnlySpan<InstanceExtra> extras
    )
    {
        int count = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            GpuInstance row = rows[i];
            InstanceExtra extra = extras[i];
            if (row.Model == 0 || extra.Acceleration == 0)
                continue;

            Matrix4x4 world = row.World;
            _rows[count++] = new GpuAccelerationInstance
            {
                Row0 = new(world.M11, world.M21, world.M31, world.M41),
                Row1 = new(world.M12, world.M22, world.M32, world.M42),
                Row2 = new(world.M13, world.M23, world.M33, world.M43),
                CustomIndexAndMask = (uint)i | (0xFFu << 24),
                OffsetAndFlags = extra.MeshOffset | (GpuAccelerationInstance.FlagNoOpaque << 24),
                Reference = extra.Acceleration,
            };
        }

        _acceleration = count == 0 ? GpuAcceleration.None : structure;
        if (count == 0)
            return;

        graph.Upload(instances, _rows.AsSpan(0, count));
        graph.BuildAcceleration(structure, (uint)count);
    }

    /// <summary>
    /// Each of the view's <paramref name="materialCount"/> material rows averaged into <paramref name="hitMaterials"/>,
    /// a pass for each material shader, which fills the rows it draws.
    /// </summary>
    private void RecordHitMaterials(RenderGraph graph, in GpuView view, GpuBuffer hitMaterials, int materialCount)
    {
        for (int shader = _averageShaders.Count; shader < view.MaterialShaders.Count; shader++)
        {
            _averageShaders.Add(_averageShader with { TypeArguments = [view.MaterialShaders[shader]] });
        }

        uint groups = (uint)((materialCount + AverageGroupSize - 1) / AverageGroupSize);
        for (int shader = 0; shader < _averageShaders.Count; shader++)
        {
            graph
                .Compute(
                    "Average materials",
                    _averageShaders[shader],
                    new GpuConstants(view.Frame.Id, hitMaterials.Id, (uint)shader, (uint)materialCount),
                    RenderStage.Prepare
                )
                .Reads(view.Frame, view.Materials)
                .Reads(view.HeapTextures.AsSpan())
                .Writes(hitMaterials)
                .Dispatch(groups);
        }
    }
}
