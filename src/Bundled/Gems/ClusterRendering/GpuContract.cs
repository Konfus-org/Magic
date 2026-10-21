using Magic.Contexts.Assets;
using Magic.Contexts.Gpu;

namespace Magic.ClusterRendering;

/// <summary>
/// Every resource the renderer provides to passes, each declared once, its name and shape together: the frame-wide
/// tables and buffers, the texture pools, the shadows' pool and tables, each view's targets, and the target an overlay
/// draws into, with how the renderer uses what it makes for each; and how the shaders read what is in them, a
/// material's bins and a texture reference that names none. Whoever makes or finds one names it by its entry here
/// (<see cref="GpuResources"/>), so no name, shape or use is written twice; a pass's own come from its
/// <see cref="PassCreate"/>.
/// </summary>
internal static class GpuContract
{
    /// <summary>
    /// What a <c>TextureRef</c> holds for no texture: a surface's factor alone.
    /// </summary>
    internal const uint NoTexture = uint.MaxValue;
    /// <summary>
    /// The raster bins every frame has, whose pixels do not clip: single-sided and double-sided. A surface's own
    /// clusters' come after, and its cards' <see cref="CardBins"/> after those (Shaders/Include/Culling.hlsli's
    /// CardBinOffset).
    /// </summary>
    internal const int FixedRasterBins = 2;
    /// <summary>
    /// The first surface's cards' raster bin; a surface's own clusters' bins are those before it.
    /// </summary>
    internal const int CardBins = 128;
    /// <summary>
    /// Raster bins there are rows for, a phase's in the bins' draw arguments.
    /// </summary>
    internal const int RasterBinRows = 256;

    /// <summary>
    /// The surfaces of the <see cref="FixedRasterBins"/>, in bin order: no surface, single-sided then double-sided.
    /// </summary>
    internal static IReadOnlyList<SurfaceKey> FixedRasterKeys { get; } =
        [new SurfaceKey(Handle<Shader>.None, false, false), new SurfaceKey(Handle<Shader>.None, false, true)];

    internal static ResourceInfo Instances { get; } =
        FrameBuffer("Instances", "GpuInstance", GpuBufferUsage.GraphicsRead | GpuBufferUsage.ComputeRead);

    internal static ResourceInfo Models { get; } =
        FrameBuffer("Models", "GpuModel", GpuBufferUsage.GraphicsRead | GpuBufferUsage.ComputeRead);

    internal static ResourceInfo Levels { get; } =
        FrameBuffer("Levels", "GpuLevel", GpuBufferUsage.GraphicsRead | GpuBufferUsage.ComputeRead);

    internal static ResourceInfo Clusters { get; } =
        FrameBuffer("Clusters", "GpuCluster", GpuBufferUsage.GraphicsRead | GpuBufferUsage.ComputeRead);

    internal static ResourceInfo Cards { get; } =
        FrameBuffer("Cards", "GpuCard", GpuBufferUsage.GraphicsRead | GpuBufferUsage.ComputeRead);

    internal static ResourceInfo Geometry { get; } =
        FrameBuffer("Geometry", "uint", GpuBufferUsage.GraphicsRead | GpuBufferUsage.ComputeRead);

    internal static ResourceInfo Materials { get; } =
        FrameBuffer("Materials", "GpuMaterial", GpuBufferUsage.GraphicsRead | GpuBufferUsage.ComputeRead);

    internal static ResourceInfo MaterialBins { get; } =
        FrameBuffer("MaterialBins", "uint", GpuBufferUsage.GraphicsRead | GpuBufferUsage.ComputeRead);

    internal static ResourceInfo MaterialGi { get; } =
        FrameBuffer("MaterialGi", "GpuMaterialGi", GpuBufferUsage.ComputeRead);

    /// <summary>
    /// The texture pools the materials' textures live in (<see cref="GpuResources"/>), which a surface's shader reads
    /// by name (Shaders/Include/SurfaceBindings.hlsli): colour (sRGB) then data (linear), each 256, 512, 1024 and 2048
    /// texels a side, sampled as the pools' own sampler says, which the frame binds with them.
    /// </summary>
    internal static IReadOnlyList<ResourceInfo> TexturePools { get; } =
    [
        PoolTexture("PoolSrgb256", GpuFormat.Bc7Srgb),
        PoolTexture("PoolSrgb512", GpuFormat.Bc7Srgb),
        PoolTexture("PoolSrgb1024", GpuFormat.Bc7Srgb),
        PoolTexture("PoolSrgb2048", GpuFormat.Bc7Srgb),
        PoolTexture("PoolLinear256", GpuFormat.Bc7Unorm),
        PoolTexture("PoolLinear512", GpuFormat.Bc7Unorm),
        PoolTexture("PoolLinear1024", GpuFormat.Bc7Unorm),
        PoolTexture("PoolLinear2048", GpuFormat.Bc7Unorm),
    ];

    internal static ResourceInfo MeshFieldAtlas { get; } = new(
        "MeshFieldAtlas",
        false,
        true,
        GpuFormat.R16Float,
        GpuTextureKind.Texture3D,
        string.Empty,
        TextureUsage: GpuTextureUsage.Sampled);

    internal static ResourceInfo MeshFields { get; } =
        FrameBuffer("MeshFields", "GpuMeshField", GpuBufferUsage.ComputeRead);

    internal static ResourceInfo GiLevels { get; } =
        FrameBuffer("GiLevels", "GpuGiLevel", GpuBufferUsage.ComputeRead | GpuBufferUsage.GraphicsRead);

    internal static ResourceInfo WorldLights { get; } =
        FrameBuffer("WorldLights", "GpuWorldLight", GpuBufferUsage.ComputeRead);

    internal static ResourceInfo ChangedLights { get; } =
        FrameBuffer("ChangedLights", "uint", GpuBufferUsage.ComputeRead);

    internal static ResourceInfo ShadowPool { get; } = new(
        "ShadowPool",
        false,
        true,
        GpuFormat.D32Float,
        GpuTextureKind.Texture2D,
        string.Empty,
        PassFilter.Comparison,
        TextureUsage: GpuTextureUsage.DepthTarget | GpuTextureUsage.Sampled);

    internal static ResourceInfo ShadowTables { get; } = FrameBuffer(
        "ShadowTables",
        "ShadowEntry",
        GpuBufferUsage.ComputeRead | GpuBufferUsage.ComputeWrite | GpuBufferUsage.GraphicsRead);

    internal static ResourceInfo ShadowClipmap { get; } =
        FrameBuffer("ShadowClipmap", "GpuClipmap", GpuBufferUsage.ComputeRead | GpuBufferUsage.GraphicsRead);

    internal static ResourceInfo Moved { get; } = FrameBuffer("Moved", "float4", GpuBufferUsage.ComputeRead);

    internal static ResourceInfo ZBinsEmpty { get; } = FrameBuffer("ZBinsEmpty", "uint", GpuBufferUsage.ComputeRead);

    internal static ResourceInfo Zeros { get; } = FrameBuffer("Zeros", "uint", GpuBufferUsage.ComputeRead);

    internal static ResourceInfo Failures { get; } = FrameBuffer(
        "Failures",
        "uint",
        GpuBufferUsage.ComputeRead | GpuBufferUsage.ComputeWrite | GpuBufferUsage.GraphicsRead);

    internal static ResourceInfo Depth { get; } =
        ViewTexture("Depth", GpuFormat.D32Float, GpuTextureUsage.DepthTarget | GpuTextureUsage.Sampled);

    internal static ResourceInfo Visibility { get; } = ViewTexture(
        "Visibility",
        GpuFormat.R32Uint,
        GpuTextureUsage.ColorTarget | GpuTextureUsage.GraphicsRead | GpuTextureUsage.ComputeRead);

    internal static ResourceInfo Albedo { get; } =
        ViewTexture("Albedo", GpuFormat.Rgba8Srgb, GpuTextureUsage.ColorTarget | GpuTextureUsage.Sampled);

    internal static ResourceInfo Normal { get; } =
        ViewTexture("Normal", GpuFormat.Rgb10A2Unorm, GpuTextureUsage.ColorTarget | GpuTextureUsage.Sampled);

    internal static ResourceInfo Material { get; } =
        ViewTexture("Material", GpuFormat.Rgba8Unorm, GpuTextureUsage.ColorTarget | GpuTextureUsage.Sampled);

    internal static ResourceInfo Hdr { get; } =
        ViewTexture("Hdr", GpuFormat.Rgba16Float, GpuTextureUsage.ColorTarget | GpuTextureUsage.Sampled);

    internal static ResourceInfo Velocity { get; } =
        ViewTexture("Velocity", GpuFormat.Rg16Float, GpuTextureUsage.ColorTarget | GpuTextureUsage.Sampled);

    internal static ResourceInfo Exposure { get; } = new(
        "Exposure",
        true,
        false,
        GpuFormat.Invalid,
        GpuTextureKind.Texture2D,
        "float4",
        BufferUsage: GpuBufferUsage.GraphicsRead | GpuBufferUsage.ComputeRead | GpuBufferUsage.ComputeWrite);

    /// <summary>
    /// What a target's passes draw into (<see cref="PassStage.Overlay"/>): the window's image or a render texture,
    /// every view in it composed, whose format and encoding are the target's own and known only as it is drawn into,
    /// so only a script pass, which makes its pipelines for the target it is given, draws into it.
    /// </summary>
    internal static ResourceInfo Target { get; } =
        new("Target", false, false, GpuFormat.Rgba16Float, GpuTextureKind.Texture2D, string.Empty, IsTarget: true);

    /// <summary>
    /// Every entry above, by name: what a pass sees of the renderer's own. Declared last, as it is made from them.
    /// </summary>
    internal static IReadOnlyDictionary<string, ResourceInfo> All { get; } = MakeAll();

    private static Dictionary<string, ResourceInfo> MakeAll()
    {
        List<ResourceInfo> all =
        [
            Instances, Models, Levels, Clusters, Cards, Geometry, Materials, MaterialBins, MaterialGi, .. TexturePools,
            MeshFieldAtlas, MeshFields, GiLevels, WorldLights, ChangedLights, ShadowPool, ShadowTables, ShadowClipmap,
            Moved, ZBinsEmpty, Zeros, Failures, Depth, Visibility, Albedo, Normal, Material, Hdr, Velocity, Exposure,
            Target,
        ];
        Dictionary<string, ResourceInfo> byName = new(StringComparer.Ordinal);
        foreach (ResourceInfo info in all)
        {
            byName.Add(info.Name, info);
        }

        return byName;
    }

    private static ResourceInfo FrameBuffer(string name, string type, GpuBufferUsage usage)
        => new(name, true, true, GpuFormat.Invalid, GpuTextureKind.Texture2D, type, BufferUsage: usage);

    private static ResourceInfo PoolTexture(
        string name,
        GpuFormat format) => new(
            name,
            false,
            true,
            format,
            GpuTextureKind.Texture2DArray,
            string.Empty,
            TextureUsage: GpuTextureUsage.Sampled);

    private static ResourceInfo ViewTexture(string name, GpuFormat format, GpuTextureUsage usage)
        => new(name, false, false, format, GpuTextureKind.Texture2D, string.Empty, TextureUsage: usage);
}
