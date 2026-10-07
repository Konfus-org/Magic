using Magic.Contexts.Rendering;
namespace DeferredRendererGem;

/// <summary>
/// What the scene is drawn into before it is lit: every surface in view, as the material it is and where it is, one
/// texture per property (<c>Include/GBuffer.hlsli</c> writes and reads them). Nothing here is lit; the lighting pass
/// reads all of it for each pixel and writes the lit scene into <c>Hdr</c>. Its textures (the colours below and the
/// depth) are among a render target's <see cref="FrameTargets.EngineTextures"/>, and each is a pass input by its name.
/// </summary>
internal static class GBuffer
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
}
