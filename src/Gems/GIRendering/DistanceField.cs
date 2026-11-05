using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Magic.Debugging;
using Magic.Gpu;
using Magic.Rendering;
using Magic.Shaders;
using Magic.Utils;

namespace Magic.GIRendering;

/// <summary>
/// The global distance field: the atlas every resident model's field is written into, four clipmaps of voxels around
/// the camera, the tables of their bricks and the passes that keep them: bricks that slid out of the window or that
/// an instance moved through are listed, the instances over each listed, and the bricks made again from those
/// instances' fields. A still world updates nothing.
/// </summary>
internal sealed class DistanceField(IGraphicsApi gpu, RenderGraph graph, GpuPipelines pipelines) : IDisposable
{
    public const int Levels = 4;

    public const int Voxels = 128;

    public const int BrickVoxels = 8;

    public const int Bricks = Voxels / BrickVoxels;

    public const int BricksPerLevel = Bricks * Bricks * Bricks;

    public const int InstancesPerBrick = 64;

    public const int AtlasVoxels = 384;

    public const int ModelVoxels = MeshDerived.FieldSize;

    public const int SlotsPerRow = AtlasVoxels / ModelVoxels;

    public const int ModelSlots = SlotsPerRow * SlotsPerRow * SlotsPerRow;

    public const int MaxDirtyBricks = 4096;

    public const float FinestVoxel = 0.125f;

    private const int CountsBytes = 64;

    private const int DirtyArgumentsBytes = 12;

    private const int Threads = 64;

    private const int ListGroupsPerRow = 65535;

    private readonly GpuPass _writeModelPass = pipelines.Compute("Field: write model", "Field", "FieldWriteModel");

    private readonly GpuPass _scrollPass = pipelines.Compute("Field: scroll", "Field", "FieldScroll");

    private readonly GpuPass _invalidatePass = pipelines.Compute("Field: invalidate", "Field", "FieldInvalidate");

    private readonly GpuPass _collectPass = pipelines.Compute("Field: collect", "Field", "FieldCollect");

    private readonly GpuPass _listPass = pipelines.Compute("Field: list instances", "Field", "FieldList");

    private readonly GpuPass _updatePass = pipelines.Compute("Field: update bricks", "Field", "FieldUpdate");

    private readonly GpuTexture[] _clipmaps = new GpuTexture[Levels];

    private readonly List<(MeshDerived Derived, uint Slot)> _arrived = [];

    private GpuTexture _atlas;

    private GpuBuffer _modelFields;

    private GpuBuffer _bricks;

    private GpuBuffer _lists;

    private GpuBuffer _dirtyBricks;

    private GpuBuffer _counts;

    private GpuBuffer _dirtyArguments;

    private GpuBuffer _dirtyInstances;

    private GpuBuffer _modelBytes;

    private GpuSampler _sampler;

    private int _slotCount;

    private int _dirtyInstanceCount;

    private int _instanceCount;

    private bool _isFullLogged;

    private bool _isTableReady;

    public GpuBuffer Constants { get; private set; }

    public void Dispose()
    {
        foreach (
            GpuBuffer buffer in (ReadOnlySpan<GpuBuffer>)
                [
                    Constants,
                    _modelFields,
                    _bricks,
                    _lists,
                    _dirtyBricks,
                    _counts,
                    _dirtyArguments,
                    _dirtyInstances,
                    _modelBytes,
                ]
        )
        {
            if (!buffer.IsNone)
                gpu.Destroy(buffer);
        }

        foreach (GpuTexture texture in (ReadOnlySpan<GpuTexture>)[_atlas, .. _clipmaps])
        {
            if (!texture.IsNone)
                gpu.Destroy(texture);
        }

        if (!_sampler.IsNone)
            gpu.Destroy(_sampler);
    }

    public Result Create()
    {
        (string Name, long Bytes, BufferUsages Usage)[] descriptions =
        [
            ("Field constants", FieldConstants.Bytes, BufferUsages.Storage),
            ("Model fields", (long)ModelSlots * GpuModelField.Bytes, BufferUsages.Storage),
            ("Field bricks", (long)Levels * BricksPerLevel * 16, BufferUsages.Storage),
            ("Field brick lists", (long)Levels * BricksPerLevel * (InstancesPerBrick + 1) * 4, BufferUsages.Storage),
            ("Field dirty bricks", (long)MaxDirtyBricks * 4, BufferUsages.Storage),
            ("Field counts", CountsBytes, BufferUsages.Storage | BufferUsages.Indirect),
            ("Field dirty arguments", DirtyArgumentsBytes, BufferUsages.Storage | BufferUsages.Indirect),
            ("Field dirty instances", (long)InstanceTable.Capacity * DirtyInstance.Bytes, BufferUsages.Storage),
            ("Field model bytes", (long)ModelVoxels * ModelVoxels * ModelVoxels, BufferUsages.Storage),
        ];
        GpuBuffer[] made = new GpuBuffer[descriptions.Length];
        for (int i = 0; i < descriptions.Length; i++)
        {
            Result<GpuBuffer> buffer = gpu.CreateBuffer(
                new BufferDescription
                {
                    Bytes = descriptions[i].Bytes,
                    Usage = descriptions[i].Usage,
                    Name = descriptions[i].Name,
                }
            );
            if (!buffer.Ok)
                return Result.Failure($"the {descriptions[i].Name} buffer was not made: {buffer.Error}");

            made[i] = buffer.Value;
        }

        (
            Constants,
            _modelFields,
            _bricks,
            _lists,
            _dirtyBricks,
            _counts,
            _dirtyArguments,
            _dirtyInstances,
            _modelBytes
        ) = (made[0], made[1], made[2], made[3], made[4], made[5], made[6], made[7], made[8]);
        Result<GpuTexture> atlas = CreateVolume("Model field atlas", AtlasVoxels);
        if (!atlas.Ok)
            return Result.Failure($"the model field atlas was not made: {atlas.Error}");

        _atlas = atlas.Value;
        for (int level = 0; level < Levels; level++)
        {
            Result<GpuTexture> clipmap = CreateVolume($"Field clipmap {level}", Voxels);
            if (!clipmap.Ok)
                return Result.Failure($"a field clipmap was not made: {clipmap.Error}");

            _clipmaps[level] = clipmap.Value;
        }

        Result<GpuSampler> sampler = gpu.CreateSampler(new SamplerDescription { HasMipmapBlend = false });
        if (!sampler.Ok)
            return Result.Failure($"the field sampler was not made: {sampler.Error}");

        _sampler = sampler.Value;

        return Result.Success();
    }

    /// <summary>
    /// A slot of the atlas for a model's field, written in the next upload; 0 when the atlas is full.
    /// </summary>
    public uint Add(MeshDerived derived)
    {
        if (_slotCount >= ModelSlots)
        {
            if (!_isFullLogged)
                Log.Error($"Some models cast no field shadows: the field atlas holds {ModelSlots} fields.");

            _isFullLogged = true;

            return 0;
        }

        uint slot = (uint)_slotCount++;
        _arrived.Add((derived, slot));

        return slot + 1;
    }

    /// <summary>
    /// The frame's constants uploaded, the counts reset and the models that arrived written into the atlas, in a
    /// transfer pass the caller opened, then as passes of their own.
    /// </summary>
    public void Prepare(
        GpuBuffer frameConstants,
        Vector3 cameraPosition,
        ReadOnlySpan<DirtyInstance> dirty,
        int instanceCount
    )
    {
        if (!_isTableReady)
        {
            _isTableReady = true;
            graph.Use(_bricks, GpuAccess.CopyDestination);
            graph.Recorder.FillBuffer(_bricks, 0);
        }

        _dirtyInstanceCount = Math.Min(dirty.Length, InstanceTable.Capacity);
        _instanceCount = instanceCount;
        FieldConstants constants = new()
        {
            Atlas = _atlas.Id,
            ModelFields = _modelFields.Id,
            Bricks = _bricks.Id,
            Lists = _lists.Id,
            DirtyBricks = _dirtyBricks.Id,
            Counts = _counts.Id,
            DirtyArguments = _dirtyArguments.Id,
            Sampler = _sampler.Id,
            Clipmap0 = _clipmaps[0].Id,
            Clipmap1 = _clipmaps[1].Id,
            Clipmap2 = _clipmaps[2].Id,
            Clipmap3 = _clipmaps[3].Id,
            MaxDirtyBricks = MaxDirtyBricks,
            InstanceCount = (uint)instanceCount,
            DirtyInstances = _dirtyInstances.Id,
            DirtyInstanceCount = (uint)_dirtyInstanceCount,
        };
        for (int level = 0; level < Levels; level++)
        {
            float voxel = FinestVoxel * MathF.Pow(2, level);
            constants.VoxelSizes[level] = voxel;
            Vector3 cameraVoxel = cameraPosition / voxel;
            constants.Origins[level] = new FieldOrigin(
                Snap((int)MathF.Floor(cameraVoxel.X) - (Voxels / 2)),
                Snap((int)MathF.Floor(cameraVoxel.Y) - (Voxels / 2)),
                Snap((int)MathF.Floor(cameraVoxel.Z) - (Voxels / 2)),
                0
            );
        }

        GpuUpload upload = gpu.BeginUpload(FieldConstants.Bytes);
        MemoryMarshal.Write(upload.Bytes, in constants);
        graph.Use(Constants, GpuAccess.CopyDestination);
        graph.Recorder.CopyBuffer(upload.Buffer, upload.Offset, Constants, 0, FieldConstants.Bytes);
        if (_dirtyInstanceCount > 0)
        {
            int bytes = _dirtyInstanceCount * DirtyInstance.Bytes;
            GpuUpload changes = gpu.BeginUpload(bytes);
            MemoryMarshal.AsBytes(dirty[.._dirtyInstanceCount]).CopyTo(changes.Bytes);
            graph.Use(_dirtyInstances, GpuAccess.CopyDestination);
            graph.Recorder.CopyBuffer(changes.Buffer, changes.Offset, _dirtyInstances, 0, (ulong)bytes);
        }

        graph.Use(_counts, GpuAccess.CopyDestination);
        graph.Recorder.FillBuffer(_counts, 0);
        GpuUpload arguments = gpu.BeginUpload(DirtyArgumentsBytes);
        Span<uint> groups = MemoryMarshal.Cast<byte, uint>(arguments.Bytes);
        groups[0] = 0;
        groups[1] = 1;
        groups[2] = 1;
        graph.Use(_dirtyArguments, GpuAccess.CopyDestination);
        graph.Recorder.CopyBuffer(arguments.Buffer, arguments.Offset, _dirtyArguments, 0, DirtyArgumentsBytes);

        foreach ((MeshDerived derived, uint slot) in _arrived)
        {
            GpuUpload info = gpu.BeginUpload(GpuModelField.Bytes);
            GpuModelField modelField = new()
            {
                Origin = derived.FieldOrigin,
                Voxel = derived.FieldVoxel,
                Band = derived.FieldBand,
            };
            MemoryMarshal.Write(info.Bytes, in modelField);
            graph.Use(_modelFields, GpuAccess.CopyDestination);
            graph.Recorder.CopyBuffer(
                info.Buffer,
                info.Offset,
                _modelFields,
                slot * GpuModelField.Bytes,
                GpuModelField.Bytes
            );
        }

        graph.EndPass();
        foreach ((MeshDerived derived, uint slot) in _arrived)
        {
            GpuUpload bytes = gpu.BeginUpload(derived.Field.Length);
            derived.Field.AsSpan().CopyTo(bytes.Bytes);
            graph.BeginTransferPass($"Field: upload model {slot}");
            graph.Use(_modelBytes, GpuAccess.CopyDestination);
            graph.Recorder.CopyBuffer(bytes.Buffer, bytes.Offset, _modelBytes, 0, (ulong)derived.Field.Length);
            graph.EndPass();
            _writeModelPass
                .Run(
                    new GpuConstants(frameConstants.Id, _modelBytes.Id, slot, Constants.Id),
                    name: $"Field: write model {slot}"
                )
                .Reads(frameConstants, Constants, _modelBytes)
                .Writes(_atlas)
                .Dispatch(ModelVoxels / 8, ModelVoxels / 8, ModelVoxels / 8);
            Log.Verbose($"The field atlas took a model in slot {slot}.");
        }

        _arrived.Clear();
        graph.BeginTransferPass("Field prepared");
    }

    /// <summary>
    /// The clipmaps brought up to date, after the frame's instances are uploaded.
    /// </summary>
    public void Record(in RenderView view)
    {
        GpuBuffer frameConstants = view.Constants;
        GpuBuffer instances = view.Instances;
        GpuConstants header = new(frameConstants.Id, 0, 0, Constants.Id);
        int cells = Levels * BricksPerLevel;

        _scrollPass.Run(header).Reads(Constants).Writes(_bricks).Dispatch(Groups(cells));

        if (_dirtyInstanceCount > 0)
        {
            _invalidatePass
                .Run(header)
                .Reads(Constants, _dirtyInstances)
                .Writes(_bricks)
                .Dispatch(Groups(_dirtyInstanceCount));
        }

        _collectPass
            .Run(header)
            .Reads(Constants, _bricks)
            .Writes(_dirtyBricks, _lists, _counts, _dirtyArguments)
            .Dispatch(Groups(cells));

        if (_instanceCount > 0)
        {
            GpuPassRun listRun = _listPass
                .Run(header)
                .Reads(frameConstants, Constants, instances, _bricks)
                .Writes(_lists);
            int groups = (int)Groups(_instanceCount);
            int rows = (groups + ListGroupsPerRow - 1) / ListGroupsPerRow;
            listRun.Dispatch((uint)Math.Min(groups, ListGroupsPerRow), (uint)rows);
        }

        GpuPassRun updateRun = _updatePass
            .Run(header)
            .Reads(frameConstants, Constants, instances, _modelFields, _atlas, _lists, _dirtyBricks, _counts)
            .Writes(_bricks);
        foreach (GpuTexture clipmap in _clipmaps)
        {
            updateRun.Writes(clipmap);
        }

        updateRun.Indirect(_dirtyArguments).DispatchIndirect(_dirtyArguments, 0);
    }

    /// <summary>
    /// Says a pass traces the field: what it declares before sampling.
    /// </summary>
    public void UseForSampling()
    {
        graph.Use(Constants, GpuAccess.ShaderRead);
        foreach (GpuTexture clipmap in _clipmaps)
        {
            graph.Use(clipmap, GpuAccess.ShaderRead);
        }
    }

    /// <summary>
    /// Says a pass reads the instances that changed this frame: what redoes its own tables under them.
    /// </summary>
    public void UseForChanges()
    {
        graph.Use(Constants, GpuAccess.ShaderRead);
        graph.Use(_dirtyInstances, GpuAccess.ShaderRead);
    }

    private static uint Groups(int count) => (uint)((count + Threads - 1) / Threads);

    /// <summary>
    /// The voxel at or below one, on a brick's boundary, so the window moves a brick at a time.
    /// </summary>
    private static int Snap(int voxel) => (int)Math.Floor(voxel / (double)BrickVoxels) * BrickVoxels;

    private Result<GpuTexture> CreateVolume(string name, int size) =>
        gpu.CreateTexture(
            new TextureDescription
            {
                Width = size,
                Height = size,
                Depth = size,
                Kind = TextureKind.Texture3D,
                Format = GpuFormat.R8Snorm,
                Usage = TextureUsages.Storage | TextureUsages.Sampled,
                Name = name,
            }
        );
}

/// <summary>
/// What the field passes read, the twin of <c>Scene/Field.slang</c>'s.
/// </summary>
[ShaderStruct]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct FieldConstants
{
    public const int Bytes = 144;

    public FieldOrigins Origins;

    public FieldVoxelSizes VoxelSizes;

    public uint Atlas;

    public uint ModelFields;

    public uint Bricks;

    public uint Lists;

    public uint DirtyBricks;

    public uint Counts;

    public uint DirtyArguments;

    public uint Sampler;

    public uint Clipmap0;

    public uint Clipmap1;

    public uint Clipmap2;

    public uint Clipmap3;

    public uint MaxDirtyBricks;

    public uint InstanceCount;

    public uint DirtyInstances;

    public uint DirtyInstanceCount;
}

/// <summary>
/// A level's window: the voxel, counted in that level's voxels, at its corner.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct FieldOrigin(int X, int Y, int Z, int W);

[InlineArray(DistanceField.Levels)]
internal struct FieldOrigins
{
    private FieldOrigin _first;
}

[InlineArray(DistanceField.Levels)]
internal struct FieldVoxelSizes
{
    private float _first;
}

/// <summary>
/// A model's field in the atlas, the twin of <c>Scene/Field.slang</c>'s.
/// </summary>
[ShaderStruct]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct GpuModelField
{
    public const int Bytes = 32;

    public Vector3 Origin;

    public float Voxel;

    public float Band;

    public uint Padding0;

    public uint Padding1;

    public uint Padding2;
}
