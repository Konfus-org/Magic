using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Magic.Contexts.Assets;
using Magic.Contexts.Gpu;
using Magic.Interfaces;

namespace Magic.Contexts.Rendering;

/// <summary>
/// The view's light turned into the colours a display shows, bright light rolling off to white rather than clipping,
/// shown <see cref="Bias"/> stops brighter (more than zero) or darker than its exposure says.
/// </summary>
public readonly record struct Tonemap(Handle<Shader> Shader, float Bias = 0) : IPixelEffect
{
    public int WriteValues(Span<byte> values)
    {
        MemoryMarshal.Write(values, new TonemapValues(Bias));

        return Unsafe.SizeOf<TonemapValues>();
    }

    public GpuTexture Record(IRendering rendering, RenderView view, GpuTexture image)
        => rendering.ApplyPixelEffects(view, image, [this]);

    [ShaderStruct("Shaders/Post/Tonemap.slang", "Tonemap")]
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct TonemapValues(float Bias);
}
