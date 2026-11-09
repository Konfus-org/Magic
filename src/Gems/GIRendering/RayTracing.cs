using System.Collections.Immutable;
using System.Numerics;
using Magic.Assets;
using Magic.Rendering;
using Magic.Utils;

namespace Magic.GIRendering;

/// <summary>
/// The world as rays see it, on a GPU that traces rays: the top-level acceleration structure over the instances, an
/// instance row for every instance whose model has its triangles' structure, built again when the instances change,
/// what a ray query traces through; and the hit materials table, each material row averaged by a pass of its material
/// shader each frame, what a ray hit reads its surface from.
/// </summary>
internal sealed class RayTracing(AssetManager assets, GpuResources resources)
{
    /// <summary>
    /// The twin of <c>AverageMaterials.slang</c>'s group size, a material row each thread.
    /// </summary>
    private const int AverageGroupSize = 64;

    private readonly GpuAccelerationInstance[] _rows = new GpuAccelerationInstance[InstanceTable.Capacity];
    private readonly ShaderEntry _averageShader = PassShaders.GetEntry(assets, "AverageMaterials", "AverageMaterials");

    /// <summary>
    /// The average entry for each material shader the resources have numbered, in their order.
    /// </summary>
    private readonly List<ShaderEntry> _averageShaders = [];

    /// <summary>
    /// The structure the last build left, what the frame traces through; none while it holds no instance.
    /// </summary>
    public GpuAcceleration Acceleration { get; private set; }

    /// <summary>
    /// The structure and its instances asked for, every frame, so the structure built last stays, and the hit materials
    /// table.
    /// </summary>
    public static Result<TracedWorld> Prepare(RenderGraph graph)
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

        return Result.Success(new TracedWorld(instances.Value, acceleration.Value, hitMaterials.Value));
    }

    /// <summary>
    /// The structure built again over the instances, in a transfer pass the caller opened after the models' structures
    /// were uploaded.
    /// </summary>
    public void Build(
        RenderGraph graph,
        in TracedWorld traced,
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

        Acceleration = count == 0 ? GpuAcceleration.None : traced.Structure;
        if (count == 0)
            return;

        graph.Upload(traced.Instances, _rows.AsSpan(0, count));
        graph.BuildAcceleration(traced.Structure, (uint)count);
    }

    /// <summary>
    /// The box over the world boxes of the instances that have an acceleration structure; min over max where none do.
    /// </summary>
    public static (Vector3 Min, Vector3 Max) GetBox(ReadOnlySpan<InstanceExtra> extras)
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
    /// Each of the view's <paramref name="materialCount"/> material rows averaged into <paramref name="hitMaterials"/>,
    /// a pass for each material shader, which fills the rows it draws.
    /// </summary>
    public void RecordHitMaterials(in RenderView view, GpuBuffer hitMaterials, int materialCount)
    {
        for (int shader = _averageShaders.Count; shader < resources.MaterialShaders.Count; shader++)
        {
            _averageShaders.Add(_averageShader with { TypeArguments = [resources.MaterialShaders[shader]] });
        }

        ImmutableArray<GpuResource> heapTextures = resources.GetHeapTextures(view.Graph);
        uint groups = (uint)((materialCount + AverageGroupSize - 1) / AverageGroupSize);
        for (int shader = 0; shader < _averageShaders.Count; shader++)
        {
            view.Graph.Compute(
                    "Average materials",
                    _averageShaders[shader],
                    new GpuConstants(view.Constants.Id, hitMaterials.Id, (uint)shader, (uint)materialCount),
                    RenderStage.Prepare
                )
                .Reads(view.Constants, view.Materials)
                .Reads(heapTextures.AsSpan())
                .Writes(hitMaterials)
                .Dispatch(groups);
        }
    }
}

/// <summary>
/// The world's top-level acceleration structure, the buffer its instances are built from, and the hit materials table.
/// </summary>
internal readonly record struct TracedWorld(GpuBuffer Instances, GpuAcceleration Structure, GpuBuffer HitMaterials);

/// <summary>
/// What the CPU keeps beside an instance's row: its model's acceleration structure's address, 0 when it has none,
/// where its meshes start in the mesh table, and the box around it in the world.
/// </summary>
internal readonly record struct InstanceExtra(ulong Acceleration, uint MeshOffset, (Vector3 Min, Vector3 Max) Box);
