using System.Numerics;
using Magic.Rendering;
using Magic.Utils;

namespace Magic.GIRendering;

/// <summary>
/// The top-level acceleration structure over the instances, on a GPU that traces rays: an instance row for every
/// instance whose model has its triangles' structure, built again when the instances change, what a ray query traces
/// through.
/// </summary>
internal sealed class RayTracing
{
    private readonly GpuAccelerationInstance[] _rows = new GpuAccelerationInstance[InstanceTable.Capacity];

    /// <summary>
    /// The structure the last build left, what the frame traces through; none while it holds no instance.
    /// </summary>
    public GpuAcceleration Acceleration { get; private set; }

    /// <summary>
    /// The structure and its instances asked for, and the structure built again over the instances, in a transfer pass
    /// the caller opened after the models' structures were uploaded.
    /// </summary>
    public Result Build(RenderGraph graph, ReadOnlySpan<GpuInstance> rows, ReadOnlySpan<InstanceExtra> extras)
    {
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

        int count = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            GpuInstance row = rows[i];
            InstanceExtra extra = extras[i];
            if (row.ClusterCount == 0 || extra.Acceleration == 0)
                continue;

            Matrix4x4 world = row.World;
            _rows[count++] = new GpuAccelerationInstance
            {
                Row0 = new(world.M11, world.M21, world.M31, world.M41),
                Row1 = new(world.M12, world.M22, world.M32, world.M42),
                Row2 = new(world.M13, world.M23, world.M33, world.M43),
                CustomIndexAndMask = (uint)i | (0xFFu << 24),
                OffsetAndFlags = extra.LeafOffset | (GpuAccelerationInstance.FlagNoFaceCull << 24),
                Reference = extra.Acceleration,
            };
        }

        Acceleration = count == 0 ? GpuAcceleration.None : acceleration.Value;
        if (count == 0)
            return Result.Success();

        graph.Upload(instances.Value, _rows.AsSpan(0, count));
        graph.BuildAcceleration(acceleration.Value, (uint)count);

        return Result.Success();
    }
}

/// <summary>
/// What the CPU keeps beside an instance's row: its model's acceleration structure's address, 0 when it has none,
/// and where its leaf clusters start in the leaf table.
/// </summary>
internal readonly record struct InstanceExtra(ulong Acceleration, uint LeafOffset);
