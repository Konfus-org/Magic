using Magic.Contexts.Assets;

namespace Magic.Interfaces;

/// <summary>
/// A post effect that changes each pixel by itself, which a renderer runs in one pass with the pixel effects next to it
/// (<see cref="IRendering.ApplyPixelEffects"/>): its shader, a module whose struct, named like its file, implements
/// Shaders/Core/Post.slang's <c>IPixelEffect</c>, and the values that struct holds. Recorded alone, it runs as a pass
/// of its own. A project's own is a struct and a shader:
/// <code>
/// public readonly record struct Fade(Handle&lt;Shader&gt; Shader, float Amount) : IPixelEffect
/// {
///     public int WriteValues(Span&lt;byte&gt; values)
///     {
///         MemoryMarshal.Write(values, Amount);
///
///         return sizeof(float);
///     }
///
///     public GpuTexture Record(IRendering rendering, RenderView view, GpuTexture image)
///         =&gt; rendering.ApplyPixelEffects(view, image, [this]);
/// }
/// </code>
/// </summary>
public interface IPixelEffect : IPostEffect
{
    Handle<Shader> Shader { get; }

    /// <summary>
    /// Writes the effect's values into <paramref name="values"/>, laid out as its shader's struct, and returns how many
    /// bytes they take.
    /// </summary>
    int WriteValues(Span<byte> values);
}
