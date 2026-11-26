using Magic.DeferredRendering.View;
using Magic.Graphics;

namespace Magic.DeferredRendering;

/// <summary>
/// The view's ambient occlusion: how much of the ambient light reaches each pixel, from 0 to 1, and the mean direction
/// it comes from.
/// </summary>
/// <remarks>
/// Both are at the picture's size. <see cref="Occlusion"/> is <c>R8Unorm</c>. <see cref="BentNormal"/> is
/// <c>R8G8B8A8Unorm</c>, an octahedral unit vector in world axes in red and green. The shading's step writes their ids
/// into the frame's buffer as <see cref="GpuAmbient"/>, and multiplies only the ambient light by them, never the
/// direct light.
/// </remarks>
public readonly record struct AmbientOcclusion(GpuTexture Occlusion, GpuTexture BentNormal);
