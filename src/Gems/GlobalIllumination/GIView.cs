using System.Collections.Immutable;
using Magic.Assets;
using Magic.Rendering;

namespace Magic.GlobalIllumination;

/// <summary>
/// What a frame's passes read and write beyond the gem's own: the frame's buffer, the gem's constants and the indirect
/// light, the pass header's x, y and z; the view's depth and GBuffer at the picture's size, and whether the gem's history
/// holds the view at that size; and what a ray reads of the world: the sky's tables, the lights and the models.
/// </summary>
internal readonly record struct GIView(
    RenderGraph Graph,
    GpuBuffer Frame,
    GpuBuffer Constants,
    GpuBuffer IndirectLight,
    int Width,
    int Height,
    bool HasHistory,
    GpuTexture Depth,
    GpuTexture GBufferNormal,
    GpuTexture GBufferSurface,
    GpuTexture Velocity,
    ImmutableArray<GpuResource> World
)
{
    public GpuConstants Header => new(Frame.Id, Constants.Id, IndirectLight.Id);

    /// <summary>
    /// The traced textures' size: half the picture's, at least 1, the twin of <c>Light/Indirect.slang</c>'s.
    /// </summary>
    public (int Width, int Height) TracedSize => (Math.Max(Width / 2, 1), Math.Max(Height / 2, 1));
}

/// <summary>
/// The entries the gem's passes run, in the modules of its <c>Shaders/</c> folder, and the library shaders an entry is
/// specialised with.
/// </summary>
internal static class PassShaders
{
    public static ShaderEntry GetEntry(AssetManager assets, string module, string name) =>
        new()
        {
            Module = assets.Find<Shader>($"Shaders/{module}.slang"),
            Name = name,
            Stage = ShaderStage.Compute,
        };

    /// <summary>
    /// The atmosphere, for an entry generic over the sky it shades with.
    /// </summary>
    public static ShaderTypeArgument GetAtmosphere(AssetManager assets) =>
        new(assets.Find<Shader>("Shaders/Sky/Atmosphere.slang"), "Atmosphere");
}

/// <summary>
/// The descriptions the gem's own buffers and textures are asked for by, each under its name.
/// </summary>
internal static class GpuDescriptions
{
    public static BufferDescription GetBuffer(string name, long bytes, BufferUsages usage = BufferUsages.Storage) =>
        new()
        {
            Bytes = bytes,
            Usage = usage,
            Name = name,
        };

    public static TextureDescription GetTexture(string name, int width, int height, GpuFormat format) =>
        new()
        {
            Width = width,
            Height = height,
            Format = format,
            Usage = TextureUsages.Storage | TextureUsages.Sampled,
            Name = name,
        };
}
