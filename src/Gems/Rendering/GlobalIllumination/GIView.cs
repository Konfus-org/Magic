using System.Collections.Immutable;
using Magic.DeferredRendering;
using Magic.DeferredRendering.View;
using Magic.Graphics;

namespace Magic.GlobalIllumination;

/// <summary>
/// What a frame's passes read and write beyond the gem's own textures, the reflections' passes too.
/// </summary>
/// <remarks>
/// The view's frame buffer holds the indirect light, and its depth and GBuffer are at the picture's size. The gem's
/// constants are the pass header's y, after the frame buffer's x. A ray reads the sky's tables, the lights and the
/// models. A pass generic over the sky is specialised on <paramref name="SkyType"/>.
/// </remarks>
public readonly record struct GIView(
    RenderGraph Graph,
    int Index,
    GpuView Gpu,
    GpuBuffer Constants,
    (int Width, int Height) TracedSize,
    ImmutableArray<GpuResource> World,
    ImmutableArray<ShaderTypeArgument> SkyType
)
{
    /// <summary>
    /// The view depth's format: the depth in red, the share of the diffuse rays that reached the sky in green, and the
    /// frames that share has blended in blue.
    /// </summary>
    /// <remarks>
    /// Each ask names this format: an ask of the same name in another format frees the texture that a pass still reads.
    /// </remarks>
    public const GpuFormat DepthFormat = GpuFormat.R16G16B16A16Sfloat;

    /// <summary>
    /// The view depth's two textures: the even frames write the first and the odd the second, the other being the
    /// history both blends read.
    /// </summary>
    public static ImmutableArray<string> DepthNames { get; } = ["Traced depth 0", "Traced depth 1"];

    public GpuConstants Header => new(Gpu.Frame.Id, Constants.Id);

    /// <summary>
    /// What a ray reads of the view, of the world as rays see it and of the sky's tables, where the sky made them.
    /// </summary>
    public static ImmutableArray<GpuResource> GetWorld(GpuView gpu, TracedWorld traced, in SkyTables? sky)
    {
        ReadOnlySpan<GpuResource> tables = sky is { HasTables: true } made ? [made.Transmittance, made.View] : [];

        return [.. tables, gpu.LightTable, gpu.Instances, gpu.Vertices, gpu.Indices, gpu.Meshes, traced.HitMaterials];
    }

    /// <summary>
    /// Describes a texture of the traced textures' size.
    /// </summary>
    public TextureDescription GetTraced(string name, GpuFormat format) =>
        new(name, TracedSize.Width, TracedSize.Height, format);
}
