using Magic.DeferredRendering.View;
using Magic.Graphics;

namespace Magic.DeferredRendering;

/// <summary>
/// The light that reaches the view's surfaces indirectly this frame: traced diffuse light, reflections, and the probe
/// grid.
/// </summary>
/// <remarks>
/// The diffuse light and reflections are at a part of the picture's size, and the reflections are none where they
/// aren't traced. The probe grid holds its probes and their distances. Its ids are in the frame's buffer as
/// <see cref="GpuIndirectLight"/>.
/// </remarks>
public readonly record struct IndirectLight(
    GpuTexture Diffuse,
    GpuTexture Reflections,
    GpuBuffer Probes,
    GpuTexture ProbeDepth
);
