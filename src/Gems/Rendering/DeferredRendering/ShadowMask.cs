using Magic.DeferredRendering.View;
using Magic.Graphics;

namespace Magic.DeferredRendering;

/// <summary>
/// How much of each light reaches each pixel this frame, and the colour the sun's light takes on through glass.
/// </summary>
/// <remarks>
/// The mask has the layout <c>Scene/Shadows.slang</c> reads. The tint is none where no glass tints the light. Its ids
/// are in the frame's buffer as <see cref="GpuShadows"/>.
/// </remarks>
public readonly record struct ShadowMask(GpuBuffer Mask, GpuTexture SunTint);
