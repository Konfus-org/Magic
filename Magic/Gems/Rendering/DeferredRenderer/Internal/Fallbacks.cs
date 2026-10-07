using Magic.Contexts.Rendering;
using System.Numerics;

namespace DeferredRendererGem;

/// <summary>
/// What the renderer shows for what a scene leaves out, in one place: the background where nothing is drawn and there is
/// no <see cref="Magic.Contexts.Components.Sky"/>, the ambient light without a sky, and the sun's direction without a
/// <see cref="Magic.Contexts.Components.DirectionalLight"/>. The background's HLSL twin (<c>BackgroundR</c>,
/// <c>BackgroundG</c>, <c>BackgroundB</c>) is what the sky pass draws without a sky.
/// </summary>
[Hlsl]
internal static class Fallbacks
{
    public const float BackgroundR = 0.02f;
    public const float BackgroundG = 0.03f;
    public const float BackgroundB = 0.08f;

    public static Vector4 Background => new(BackgroundR, BackgroundG, BackgroundB, 1f);

    public static Vector3 Ambient => new(0.15f, 0.16f, 0.2f);

    public static Vector3 SunDirection => Vector3.Normalize(new Vector3(0.3f, -0.8f, 0.5f));
}
