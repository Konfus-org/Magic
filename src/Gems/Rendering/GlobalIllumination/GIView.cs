using System.Collections.Immutable;
using Magic.DeferredRendering.View;
using Magic.Graphics;

namespace Magic.GlobalIllumination;

/// <summary>
/// What a frame's passes read and write beyond the gem's own textures.
/// </summary>
/// <remarks>
/// The view's frame buffer holds the indirect light, and its depth and GBuffer are at the picture's size. The gem's
/// constants are the pass header's y, after the frame buffer's x. A ray reads the sky's tables, the lights and the
/// models.
/// </remarks>
internal readonly record struct GIView(
    RenderGraph Graph,
    GpuView Gpu,
    GpuBuffer Constants,
    (int Width, int Height) TracedSize,
    ImmutableArray<GpuResource> World
)
{
    public GpuConstants Header => new(Gpu.Frame.Id, Constants.Id);

    /// <summary>
    /// Describes a texture of the traced textures' size.
    /// </summary>
    public TextureDescription GetTraced(string name, GpuFormat format) =>
        new(name, TracedSize.Width, TracedSize.Height, format);
}
