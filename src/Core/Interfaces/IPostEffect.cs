using Magic.Contexts.Gpu;
using Magic.Contexts.Rendering;

namespace Magic.Interfaces;

/// <summary>
/// A pass on a view's finished image, as a world's <see cref="Contexts.Components.PostProcessing"/> lists it: a struct
/// of its shader and the values the shader reads, which records the pass. A project's own is a struct and a shader:
/// <code>
/// public readonly record struct Pixelate(Handle&lt;Shader&gt; Shader, float Size) : IPostEffect
/// {
///     public GpuTexture Record(IRendering rendering, RenderView view, GpuTexture image)
///         =&gt; rendering.Effect(view, Shader, image, Size);
/// }
/// </code>
/// </summary>
public interface IPostEffect : IRenderEffect
{
    /// <summary>
    /// Records the effect's passes on <paramref name="image"/>, <paramref name="view"/>'s image so far, and hands back
    /// the image after it. Render thread.
    /// </summary>
    GpuTexture Record(IRendering rendering, RenderView view, GpuTexture image);
}
