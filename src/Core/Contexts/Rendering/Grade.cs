using System.Runtime.InteropServices;
using Magic.Contexts.Assets;
using Magic.Contexts.Gpu;
using Magic.Interfaces;

namespace Magic.Contexts.Rendering;

/// <summary>
/// How the finished view is graded, each zero for none: its saturation and its contrast about middle grey made more
/// (up to 1, twice) or less (down to -1: grey, or flat), a tint it is multiplied by, white by default, and how dark its
/// corners go, 0 to 1.
/// </summary>
public readonly record struct Grade(
    Handle<Shader> Shader,
    float Saturation = 0,
    float Contrast = 0,
    Color Tint = default,
    float Vignette = 0) : IPostEffect
{
    public Color Tint { get => field == default ? new Color(1, 1, 1) : field; init; } = Tint;

    public GpuTexture Record(IRendering rendering, RenderView view, GpuTexture image)
        => rendering.Effect(view, Shader, image, new GradeValues(Tint, Saturation, Contrast, Vignette));

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct GradeValues(Color Tint, float Saturation, float Contrast, float Vignette);
}
