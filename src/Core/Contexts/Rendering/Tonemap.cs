using Magic.Contexts.Assets;
using Magic.Contexts.Gpu;
using Magic.Interfaces;

namespace Magic.Contexts.Rendering;

/// <summary>
/// The view's light turned into the colours a display shows, bright light rolling off to white rather than clipping,
/// shown <see cref="Bias"/> stops brighter (more than zero) or darker than its exposure says.
/// </summary>
public readonly record struct Tonemap(Handle<Shader> Shader, float Bias = 0) : IPostEffect
{
    public GpuTexture Record(IRendering rendering, RenderView view, GpuTexture image)
        => rendering.Effect(view, Shader, image, Bias);
}
