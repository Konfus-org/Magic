using Magic.DeferredRendering.View;
using Magic.Graphics;

namespace Magic.DeferredRendering;

/// <summary>
/// The light that reaches the view's surfaces indirectly this frame: traced diffuse light and the probe grid.
/// </summary>
/// <remarks>
/// The diffuse light is at a part of the picture's size, and so is the share of the sky that each of its pixels sees,
/// in the second channel of its texture. The probe grid holds its probes and their distances. Its ids are in the
/// frame's buffer as <see cref="GpuIndirectLight"/>.
/// </remarks>
public readonly record struct IndirectLight(
    GpuTexture Diffuse,
    GpuTexture SkyShare,
    GpuBuffer Probes,
    GpuTexture ProbeDepth
);

/// <summary>
/// The light that smooth surfaces reflect this frame, traced at a part of the picture's size.
/// </summary>
/// <remarks>
/// Its id is in the frame's buffer, in <see cref="GpuIndirectLight"/>.
/// </remarks>
public readonly record struct Reflections(GpuTexture Light);
