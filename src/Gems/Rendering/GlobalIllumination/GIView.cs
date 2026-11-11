using System.Collections.Immutable;
using Magic.DeferredRendering.View;
using Magic.Graphics;

namespace Magic.GlobalIllumination;

/// <summary>
/// What a frame's passes read and write beyond the gem's own: the view, whose frame buffer holds the indirect light and
/// whose depth and GBuffer are at the picture's size; the gem's constants, the pass header's y after the frame buffer's
/// x; the traced textures' size, and whether the gem's history holds the view at that size; and what a ray reads of the
/// world: the sky's tables, the lights and the models.
/// </summary>
internal readonly record struct GIView(
    RenderGraph Graph,
    GpuView Gpu,
    GpuBuffer Constants,
    (int Width, int Height) TracedSize,
    bool HasHistory,
    ImmutableArray<GpuResource> World
)
{
    public GpuConstants Header => new(Gpu.Frame.Id, Constants.Id);

    /// <summary>
    /// The description of a texture of the traced textures' size.
    /// </summary>
    public TextureDescription GetTraced(string name, GpuFormat format) =>
        GpuDescriptions.GetTexture(name, TracedSize.Width, TracedSize.Height, format);
}
