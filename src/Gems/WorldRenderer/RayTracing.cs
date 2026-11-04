using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Gpu;
using Magic.Rendering;
using Magic.Utils;

namespace WorldRenderer;

/// <summary>
/// The top-level acceleration structure over the instances, on a GPU that traces rays: an instance row for every
/// instance whose model has its triangles' structure, built again whenever an instance or a model changed, what
/// the ray-query tracer traces through.
/// </summary>
internal sealed class RayTracing(IGraphicsApi gpu) : IDisposable
{
    private readonly GpuAccelerationInstance[] _rows = new GpuAccelerationInstance[InstanceTable.Capacity];
    private GpuBuffer _instances;
    private GpuAcceleration _acceleration;
    private int _count;
    private bool _isBuilt;

    /// <summary>
    /// The structure the frame traces through; none until it holds an instance.
    /// </summary>
    public GpuAcceleration Acceleration => _isBuilt ? _acceleration : GpuAcceleration.None;

    public void Dispose()
    {
        if (!_acceleration.IsNone)
            gpu.Destroy(_acceleration);

        if (!_instances.IsNone)
            gpu.Destroy(_instances);
    }

    public Result Create()
    {
        Result<GpuBuffer> instances = gpu.CreateBuffer(
            new BufferDescription
            {
                Bytes = (long)InstanceTable.Capacity * GpuAccelerationInstance.Bytes,
                Usage = BufferUsages.Storage,
                Name = "Acceleration instances",
            }
        );
        if (!instances.Ok)
            return Result.Failure($"the acceleration instances were not made: {instances.Error}");

        _instances = instances.Value;
        Result<GpuAcceleration> acceleration = gpu.CreateAcceleration(
            new TopLevelDescription
            {
                MaxInstances = InstanceTable.Capacity,
                Instances = _instances,
                Name = "World acceleration",
            }
        );
        if (!acceleration.Ok)
            return Result.Failure($"the world's acceleration structure was not made: {acceleration.Error}");

        _acceleration = acceleration.Value;

        return Result.Success();
    }

    /// <summary>
    /// The structure built again over the instances when any changed, in a transfer pass the caller opened after
    /// the instance table and the models' structures were uploaded.
    /// </summary>
    public void Prepare(
        RenderGraph graph,
        ReadOnlySpan<GpuInstance> rows,
        ReadOnlySpan<InstanceExtra> extras,
        bool hasChanges
    )
    {
        if (!hasChanges && _isBuilt)
            return;

        _count = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            GpuInstance row = rows[i];
            InstanceExtra extra = extras[i];
            if (row.ClusterCount == 0 || extra.Acceleration == 0)
                continue;

            Matrix4x4 world = row.World;
            _rows[_count++] = new GpuAccelerationInstance
            {
                M00 = world.M11,
                M01 = world.M21,
                M02 = world.M31,
                M03 = world.M41,
                M10 = world.M12,
                M11 = world.M22,
                M12 = world.M32,
                M13 = world.M42,
                M20 = world.M13,
                M21 = world.M23,
                M22 = world.M33,
                M23 = world.M43,
                CustomIndexAndMask = (uint)i | (0xFFu << 24),
                OffsetAndFlags = extra.LeafOffset | (GpuAccelerationInstance.FlagNoFaceCull << 24),
                Reference = extra.Acceleration,
            };
        }

        _isBuilt = _count > 0;
        if (!_isBuilt)
            return;

        int bytes = _count * GpuAccelerationInstance.Bytes;
        GpuUpload upload = gpu.BeginUpload(bytes);
        MemoryMarshal.AsBytes(_rows.AsSpan(0, _count)).CopyTo(upload.Bytes);
        graph.Use(_instances, GpuAccess.CopyDestination);
        graph.Recorder.CopyBuffer(upload.Buffer, upload.Offset, _instances, 0, (ulong)bytes);
        graph.Recorder.BuildAcceleration(_acceleration, (uint)_count);
    }
}

/// <summary>
/// What the CPU keeps beside an instance's row: its model's acceleration structure's address, 0 when it has none,
/// and where its leaf clusters start in the leaf table.
/// </summary>
internal readonly record struct InstanceExtra(ulong Acceleration, uint LeafOffset);
