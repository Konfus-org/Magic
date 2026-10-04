using Magic.Contexts.Rendering;
using Magic.Contexts.Settings;
using Magic.Interfaces;
using Magic.Mathematics;
using System.Numerics;

namespace DeferredRendererGem;

/// <summary>
/// The GI's textures and buffers: per level (stacked along Z in one texture each, so a shader binds one whatever the
/// level count) what the voxels are made of, their distance field, how much sky they see and the light in them; the
/// scratch one level is rebuilt through; and the atlas of mesh occupancy bricks. Made again when the settings change
/// their size, which starts every level over.
/// </summary>
internal sealed class GiVolumes
{
    public const int BrickSize = 16;

    public const int BricksAcross = 16;

    public const int BricksDeep = 8;

    public const int BrickCount = BricksAcross * BricksAcross * BricksDeep;

    public const int AccumWords = 8;

    public const int LightCells = 16;

    public const int LightsPerCell = 32;

    public const int TaskCapacity = 1 << 16;

    public const int TaskChunk = 4;

    public GiVolumes(IRendering gpu)
    {
        const GpuBufferUsage rw = GpuBufferUsage.ComputeRead | GpuBufferUsage.ComputeWrite;
        BrickAtlas = gpu.CreateTexture(new TextureDesc(GpuFormat.R8Unorm, GpuTextureUsage.Sampler | GpuTextureUsage.ComputeWrite, BricksAcross * BrickSize, BricksAcross * BrickSize, Layers: BricksDeep * BrickSize, Kind: GpuTextureKind.Texture3D));
        LightGrid = gpu.CreateBuffer(rw, LightCells * LightCells * LightCells * (LightsPerCell + 1) * 4);
        Tasks = gpu.CreateBuffer(rw, TaskCapacity * 8);
        TaskArgs = gpu.CreateBuffer(GpuBufferUsage.Indirect | rw, 16);
        PageList = new GrowableBuffer(gpu, rw, 256 * 4);
        PageArgs = gpu.CreateBuffer(GpuBufferUsage.Indirect | rw, 16);
        Counters = gpu.CreateBuffer(rw, 16);
    }

    public GpuTexture BrickAtlas { get; }

    public GpuBuffer LightGrid { get; }

    /// <summary>
    /// The 4 x 4 x 4 chunks of instances to stamp: instance slot and chunk index pairs, <see cref="TaskArgs"/> their dispatch.
    /// </summary>
    public GpuBuffer Tasks { get; }

    public GpuBuffer TaskArgs { get; }

    public GrowableBuffer PageList { get; }

    public GpuBuffer PageArgs { get; }

    /// <summary>
    /// What the GPU counts that the CPU may read for the stats: tasks dropped for want of room.
    /// </summary>
    public GpuBuffer Counters { get; }

    public GpuTexture Albedo { get; private set; }

    public GpuTexture Emissive { get; private set; }

    public GpuTexture Sdf { get; private set; }

    public GpuTexture SkyVis { get; private set; }

    public GpuTexture ScratchA { get; private set; }

    public GpuTexture ScratchB { get; private set; }

    /// <summary>
    /// The light as spherical harmonics, one texture per colour channel, two sets used in turn.
    /// </summary>
    public GpuTexture[][] Sh { get; } = [new GpuTexture[3], new GpuTexture[3]];

    public GpuBuffer Accum { get; private set; }

    public int Resolution { get; private set; }

    public int Levels { get; private set; }

    /// <summary>
    /// The set of <see cref="Sh"/> the lighting reads this frame; the other is written.
    /// </summary>
    public int Current { get; set; }

    public Vector3[] Origins { get; } = new Vector3[4];

    public float[] Voxels { get; } = new float[4];

    /// <summary>
    /// Bit i set once level i has been rebuilt at least once.
    /// </summary>
    public uint Valid { get; set; }

    public int UpdateLevel { get; set; }

    public Vector3 Shift { get; set; }

    public bool Made => Albedo.IsValid;

    /// <summary>
    /// A mesh box with a little thickness on any flat axis, so a plane rasterises into a slab of its brick and stamps
    /// as a thin wall rather than nothing.
    /// </summary>
    public static Aabb InflateFlat(in Aabb box)
    {
        Vector3 extent = box.Max - box.Min;
        float longest = MathF.Max(extent.X, MathF.Max(extent.Y, extent.Z));
        float least = MathF.Max(longest * 0.02f, 1e-3f);
        Vector3 pad = new(extent.X < least ? (least - extent.X) * 0.5f : 0f, extent.Y < least ? (least - extent.Y) * 0.5f : 0f, extent.Z < least ? (least - extent.Z) * 0.5f : 0f);
        return new Aabb(box.Min - pad, box.Max + pad);
    }

    /// <summary>
    /// The volumes for these settings; made again, and every level started over, when their size changed.
    /// </summary>
    public void Ensure(IRendering gpu, GiSettings settings)
    {
        Ensure(gpu, settings.Resolution, settings.Levels);
    }

    /// <summary>
    /// The smallest volumes there are, for the bindings while the GI is off; nothing when any volumes exist.
    /// </summary>
    public void EnsureStub(IRendering gpu)
    {
        if (!Made)
            Ensure(gpu, 8, 1);
    }

    private void Ensure(IRendering gpu, int resolution, int levels)
    {
        if (Made && Resolution == resolution && Levels == levels)
            return;

        ReleaseVolumes(gpu);
        Resolution = resolution;
        Levels = levels;
        uint res = (uint)Resolution, deep = (uint)(Resolution * Levels);
        const GpuTextureUsage usage = GpuTextureUsage.Sampler | GpuTextureUsage.ComputeWrite;
        Albedo = gpu.CreateTexture(new TextureDesc(GpuFormat.Rgba8Unorm, usage, res, res, Layers: deep, Kind: GpuTextureKind.Texture3D));
        Emissive = gpu.CreateTexture(new TextureDesc(GpuFormat.Rgba16Float, usage, res, res, Layers: deep, Kind: GpuTextureKind.Texture3D));
        Sdf = gpu.CreateTexture(new TextureDesc(GpuFormat.R8Unorm, usage, res, res, Layers: deep, Kind: GpuTextureKind.Texture3D));
        SkyVis = gpu.CreateTexture(new TextureDesc(GpuFormat.R8Unorm, usage, res, res, Layers: deep, Kind: GpuTextureKind.Texture3D));
        ScratchA = gpu.CreateTexture(new TextureDesc(GpuFormat.R8Unorm, usage, res, res, Layers: res, Kind: GpuTextureKind.Texture3D));
        ScratchB = gpu.CreateTexture(new TextureDesc(GpuFormat.R8Unorm, usage, res, res, Layers: res, Kind: GpuTextureKind.Texture3D));
        for (int set = 0; set < 2; set++)
        {
            for (int channel = 0; channel < 3; channel++)
                Sh[set][channel] = gpu.CreateTexture(new TextureDesc(GpuFormat.Rgba16Float, usage, res, res, Layers: deep, Kind: GpuTextureKind.Texture3D));
        }

        Accum = gpu.CreateBuffer(GpuBufferUsage.ComputeRead | GpuBufferUsage.ComputeWrite, res * res * res * AccumWords * 4);
        Valid = 0;
        Current = 0;
    }

    public void Release(IRendering gpu)
    {
        ReleaseVolumes(gpu);
        gpu.Release(BrickAtlas);
        gpu.Release(LightGrid);
        gpu.Release(Tasks);
        gpu.Release(TaskArgs);
        gpu.Release(PageList.Handle);
        gpu.Release(PageArgs);
        gpu.Release(Counters);
    }

    private void ReleaseVolumes(IRendering gpu)
    {
        foreach (GpuTexture texture in (ReadOnlySpan<GpuTexture>)[Albedo, Emissive, Sdf, SkyVis, ScratchA, ScratchB, Sh[0][0], Sh[0][1], Sh[0][2], Sh[1][0], Sh[1][1], Sh[1][2]])
            gpu.Release(texture);
        gpu.Release(Accum);
        Albedo = default;
    }
}
