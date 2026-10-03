namespace Magic.Contexts.Rendering;

/// <summary>
/// What the scene is drawn into before it is lit: every surface in view, as the material it is and where it is, one
/// texture per property (<c>Include/GBuffer.hlsli</c> writes and reads them). Nothing here is lit; the lighting pass
/// reads all of it for each pixel and writes the lit scene into <c>Hdr</c>. Part of a render target's
/// <see cref="FrameTargets"/>, which sizes it, and each texture is a pass input by its name here.
/// </summary>
internal sealed class GBuffer
{
    /// <summary>
    /// Linear light, so more than 1 fits; no alpha.
    /// </summary>
    public const GpuFormat EmissiveFormat = GpuFormat.R11G11B10Float;

    public const GpuFormat AlbedoFormat = GpuFormat.Rgba8Srgb;

    /// <summary>
    /// Ten bits an axis: banding-free on a smooth highlight, which eight are not.
    /// </summary>
    public const GpuFormat NormalFormat = GpuFormat.Rgb10A2Unorm;

    public const GpuFormat MaterialFormat = GpuFormat.Rgba8Unorm;

    /// <summary>
    /// The colour targets in the order a material pipeline writes them (<c>SV_Target0</c> first).
    /// </summary>
    public static readonly GpuFormat[] ColorFormats = [EmissiveFormat, AlbedoFormat, NormalFormat, MaterialFormat];

    public GBuffer(FrameTargets.Target emissive, FrameTargets.Target albedo, FrameTargets.Target normal, FrameTargets.Target material, FrameTargets.Target depth)
    {
        Emissive = emissive;
        Albedo = albedo;
        Normal = normal;
        Material = material;
        Depth = depth;
    }

    /// <summary>
    /// What the surface emits, which the lighting adds its light to. Cleared to the clear colour, so a pixel nothing was
    /// drawn in shows that.
    /// </summary>
    public FrameTargets.Target Emissive { get; }

    /// <summary>
    /// The base colour.
    /// </summary>
    public FrameTargets.Target Albedo { get; }

    /// <summary>
    /// The world-space normal, each axis from -1..1 packed into 0..1.
    /// </summary>
    public FrameTargets.Target Normal { get; }

    /// <summary>
    /// Roughness, metallic and occlusion in r, g and b.
    /// </summary>
    public FrameTargets.Target Material { get; }

    /// <summary>
    /// Reverse-Z depth, from which the lighting works out where the pixel is; 0 where nothing was drawn.
    /// </summary>
    public FrameTargets.Target Depth { get; }

    /// <summary>
    /// The colour targets in <see cref="ColorFormats"/> order, as a render pass takes them.
    /// </summary>
    public void Colors(Span<GpuTexture> colors)
    {
        colors[0] = Emissive.Texture;
        colors[1] = Albedo.Texture;
        colors[2] = Normal.Texture;
        colors[3] = Material.Texture;
    }
}
