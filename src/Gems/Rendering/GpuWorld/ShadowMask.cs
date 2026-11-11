using Magic.Graphics;

namespace Magic.GpuWorld;

/// <summary>
/// How much of each light reaches each pixel this frame: the mask shading reads, the layout the library's
/// <c>Scene/Shadows.slang</c> reads, and the colour the sun's light takes on through glass, none where no glass tints
/// it. Its ids are in the frame's buffer as <see cref="GpuShadows"/>.
/// </summary>
public readonly record struct ShadowMask(GpuBuffer Mask, GpuTexture SunTint);
