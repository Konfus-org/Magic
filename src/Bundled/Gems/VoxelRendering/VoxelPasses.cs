using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Magic.Contexts.Assets;
using Magic.Contexts.Gpu;
using Magic.Contexts.Rendering;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.VoxelRendering;

/// <summary>
/// The renderer's step for the voxels (<see cref="IRendering.VoxelizeScene"/>): the world as a clipmap of voxels around
/// the player's camera, kept in the renderer's own volumes from frame to frame and changed where something did
/// (Shaders/Renderer/Voxels.slang). Each frame the levels' windows follow the camera by whole bricks, and the GPU marks
/// what moved, changed or came in, and voxelizes it again, up to a budget of bricks; the frame the volumes are made, or
/// the world is emptied, every brick is voxelized at once. The volumes are made again when the settings' voxel size or
/// levels, or the world's instance slots, change. Render thread.
/// </summary>
internal sealed class VoxelPasses : IDisposable
{
    // Voxels.slang's window, bricks and brick, its level's slices, and the opacity's texel.
    private const int Window = 128;
    private const int Bricks = 16;
    private const int Brick = 8;
    private const uint LevelSlices = Window + 2;
    private const uint LevelBricks = Bricks * Bricks * Bricks;

    /// <summary>
    /// The most levels: the bits that name a level in a brick's place.
    /// </summary>
    private const int MaxLevels = 8;

    /// <summary>
    /// Bricks voxelized a frame at most, but for the frame every brick is.
    /// </summary>
    private const uint BrickBudget = 4096;

    /// <summary>
    /// Runs of 64 clusters voxelized a frame at most.
    /// </summary>
    private const uint JobCapacity = 1 << 16;

    // The counts: bricks taken, jobs, the groups done; and the bytes of the three passes' groups after the first.
    private const uint Counts = 3;
    private const uint DispatchBytes = 12;

    private const GpuBufferUsage Usage = GpuBufferUsage.ComputeRead
        | GpuBufferUsage.ComputeWrite
        | GpuBufferUsage.Arguments;

    private readonly IGpuDriver _gpu;
    private readonly Passes _passes;
    private readonly GpuPipelines _pipelines;
    private readonly VoxelRenderingSettings _settings;
    private readonly ShaderEntry _markVoxelBricks;
    private readonly ShaderEntry _listVoxelJobs;
    private readonly ShaderEntry _voxelizeClusters;
    private readonly ShaderEntry _resolveVoxelOpacity;
    private readonly GpuVoxelLevel[] _levels = new GpuVoxelLevel[MaxLevels];

    /// <summary>
    /// The volumes, for the voxel size, levels and instance slots they were made for; none before the first frame.
    /// </summary>
    private Volumes? _volumes;

    /// <summary>
    /// Whether every brick is to be voxelized the next frame the passes run: from when the volumes are made or the
    /// world is emptied until then.
    /// </summary>
    private bool _isResetWanted;

    internal VoxelPasses(
        IGpuDriver gpu,
        Passes passes,
        GpuPipelines pipelines,
        VoxelRenderingSettings settings,
        Assets assets)
    {
        _gpu = gpu;
        _passes = passes;
        _pipelines = pipelines;
        _settings = settings;
        Handle<Shader> module = assets.Find<Shader>("Shaders/Renderer/Voxels.slang");
        _markVoxelBricks = new ShaderEntry(module, "MarkVoxelBricks");
        _listVoxelJobs = new ShaderEntry(module, "ListVoxelJobs");
        _voxelizeClusters = new ShaderEntry(module, "VoxelizeClusters");
        _resolveVoxelOpacity = new ShaderEntry(module, "ResolveVoxelOpacity");
    }

    public void Dispose() => _volumes?.Release(_gpu);

    /// <summary>
    /// Brings the voxels up to date around <paramref name="centre"/>, the player's camera, for a world of
    /// <paramref name="slots"/> instance slots; every brick again when <paramref name="isEmptied"/>. Nothing changes
    /// in a frame whose passes are not all compiled yet, which leaves the frame incomplete, so the windows move and the
    /// bricks are marked only in frames the passes run.
    /// </summary>
    internal VoxelScene Voxelize(Vector3 centre, int slots, bool isEmptied)
    {
        float size = Math.Max(_settings.VoxelSize, 0.01f);
        int levels = Math.Clamp(_settings.VoxelLevels, 1, MaxLevels);
        bool isMade = _volumes is not { } kept
            || kept.VoxelSize != size
            || kept.LevelCount != levels
            || kept.Slots < slots;
        if (isMade)
        {
            _volumes?.Release(_gpu);
            _volumes = CreateVolumes(size, levels, Math.Max(slots, 1024));
        }

        Volumes v = _volumes ?? throw new InvalidOperationException("The voxels' volumes were not made.");
        _isResetWanted |= isMade || isEmptied;
        if (!IsCompiled()) return new VoxelScene(v.Opacity, v.Levels);

        bool isReset = _isResetWanted;
        _isResetWanted = false;
        PlaceLevels(v, centre, isReset);
        VoxelCulling c = new(
            new VoxelScene(v.Opacity, v.Levels),
            v.Bits,
            v.Marked,
            v.Taken,
            v.TakenList,
            v.SeenSpheres,
            v.SeenLooks,
            v.Jobs,
            v.Counts,
            v.Args,
            isReset ? (uint)levels * LevelBricks : BrickBudget);
        PassHeader header = _passes.GetFrameHeader();
        uint groups = (v.Slots + ((uint)levels * LevelBricks) + 63) / 64;
        _passes.Dispatch(
            header,
            _markVoxelBricks,
            c,
            new GpuGroups(groups),
            [v.Levels],
            [v.Marked, v.Taken, v.TakenList, v.SeenSpheres, v.SeenLooks, v.Counts, v.Args]);
        _passes.Dispatch(
            header,
            _listVoxelJobs,
            c,
            v.Args,
            0,
            [v.Levels, v.Taken, v.TakenList, v.SeenSpheres],
            [v.Bits, v.Jobs, v.Counts, v.Args]);
        _passes.Dispatch(
            header,
            _voxelizeClusters,
            c,
            v.Args,
            DispatchBytes,
            [v.Levels, v.Taken, v.Jobs, v.Counts],
            [v.Bits]);
        _passes.Dispatch(
            header,
            _resolveVoxelOpacity,
            c,
            v.Args,
            2 * DispatchBytes,
            [v.Levels, v.Bits, v.TakenList, v.Counts],
            [v.Opacity]);

        return new VoxelScene(v.Opacity, v.Levels);
    }

    /// <summary>
    /// Whether every pass's pipeline is made, each asked for, so its compile starts.
    /// </summary>
    private bool IsCompiled()
    {
        bool isCompiled = true;
        foreach (ShaderEntry pass in (ReadOnlySpan<ShaderEntry>)
            [_markVoxelBricks, _listVoxelJobs, _voxelizeClusters, _resolveVoxelOpacity])
        {
            isCompiled &= _pipelines.GetCompute(pass) != GpuPipeline.None;
        }

        return isCompiled;
    }

    /// <summary>
    /// Puts each level's window about <paramref name="centre"/>, its corner a whole number of bricks, and sends the
    /// levels with where each was the frame before; on a reset, where it was is where it is, and every brick of it is
    /// marked.
    /// </summary>
    private void PlaceLevels(Volumes v, Vector3 centre, bool isReset)
    {
        for (int level = 0; level < v.LevelCount; level++)
        {
            float voxel = v.VoxelSize * (1 << level);
            float brick = voxel * Brick;
            Vector3 corner = new Vector3(
                MathF.Floor(centre.X / brick),
                MathF.Floor(centre.Y / brick),
                MathF.Floor(centre.Z / brick)) * Brick - new Vector3(Window / 2);
            GpuVoxelLevel last = _levels[level];
            _levels[level] = new GpuVoxelLevel
            {
                Origin = corner,
                VoxelSize = voxel,
                LastOrigin = isReset ? corner : last.Origin,
                Flags = isReset ? GpuVoxelLevel.Reset : 0,
            };
        }

        _gpu.Upload(v.Levels, 0, MemoryMarshal.AsBytes(_levels.AsSpan(0, v.LevelCount)));
    }

    private Volumes CreateVolumes(float voxelSize, int levels, int slots)
    {
        uint bricks = (uint)levels * LevelBricks;
        uint brickBytes = Brick * Brick * Brick;
        GpuTexture opacity = _gpu.CreateTexture(new GpuTextureDescription(
            GpuFormat.Rgba8Unorm,
            GpuTextureUsage.ComputeWrite | GpuTextureUsage.Sampled,
            Window,
            Window,
            Layers: (uint)levels * LevelSlices,
            Kind: GpuTextureKind.Texture3D));

        return new Volumes(voxelSize, levels, (uint)slots)
        {
            Opacity = opacity,
            Levels = _gpu.CreateBuffer(GpuBufferUsage.ComputeRead, (uint)(levels * Unsafe.SizeOf<GpuVoxelLevel>())),
            Bits = _gpu.CreateZeroedBuffer(Usage, bricks * brickBytes),
            Marked = _gpu.CreateZeroedBuffer(Usage, bricks / 8),
            Taken = _gpu.CreateZeroedBuffer(Usage, bricks / 8),
            TakenList = _gpu.CreateZeroedBuffer(Usage, bricks * 4),
            SeenSpheres = _gpu.CreateZeroedBuffer(Usage, (uint)slots * 16),
            SeenLooks = _gpu.CreateZeroedBuffer(Usage, (uint)slots * 4),
            Jobs = _gpu.CreateZeroedBuffer(Usage, JobCapacity * 16),
            Counts = _gpu.CreateZeroedBuffer(Usage, Counts * 4),
            Args = _gpu.CreateZeroedBuffer(Usage, 3 * DispatchBytes),
        };
    }

    /// <summary>
    /// The voxel passes' constants, as Voxels.slang's VoxelizeConstants.
    /// </summary>
    [ShaderStruct("Shaders/Renderer/Voxels.slang", "VoxelizeConstants")]
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct VoxelCulling(
        VoxelScene Scene,
        GpuBuffer Bits,
        GpuBuffer Marked,
        GpuBuffer Taken,
        GpuBuffer TakenList,
        GpuBuffer SeenSpheres,
        GpuBuffer SeenLooks,
        GpuBuffer Jobs,
        GpuBuffer Counts,
        GpuBuffer Args,
        uint BrickBudget);

    /// <summary>
    /// The voxels' volumes and the buffers their passes keep, for the voxel size, levels and instance slots they were
    /// made for.
    /// </summary>
    private sealed class Volumes(float voxelSize, int levelCount, uint slots)
    {
        internal float VoxelSize { get; } = voxelSize;

        internal int LevelCount { get; } = levelCount;

        internal uint Slots { get; } = slots;

        internal required GpuTexture Opacity { get; init; }

        /// <summary>
        /// The levels' rows (<see cref="GpuVoxelLevel"/>), sent each frame.
        /// </summary>
        internal required GpuBuffer Levels { get; init; }

        internal required GpuBuffer Bits { get; init; }

        internal required GpuBuffer Marked { get; init; }

        internal required GpuBuffer Taken { get; init; }

        internal required GpuBuffer TakenList { get; init; }

        internal required GpuBuffer SeenSpheres { get; init; }

        internal required GpuBuffer SeenLooks { get; init; }

        internal required GpuBuffer Jobs { get; init; }

        internal required GpuBuffer Counts { get; init; }

        internal required GpuBuffer Args { get; init; }

        internal void Release(IGpuDriver gpu)
        {
            gpu.Release(Opacity);
            foreach (GpuBuffer buffer in (ReadOnlySpan<GpuBuffer>)
                [Levels, Bits, Marked, Taken, TakenList, SeenSpheres, SeenLooks, Jobs, Counts, Args])
            {
                gpu.Release(buffer);
            }
        }
    }
}
