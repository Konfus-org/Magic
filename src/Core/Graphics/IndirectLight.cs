namespace Magic.Graphics;

/// <summary>
/// The light that reaches the view's surfaces indirectly this frame: the traced diffuse light and reflections at half
/// the picture's size, and the probe grid's probes and their distances. Its ids are in the frame's buffer as
/// <see cref="GpuIndirectLight"/>.
/// </summary>
public readonly record struct IndirectLight(
    GpuTexture Diffuse,
    GpuTexture Reflections,
    GpuBuffer Probes,
    GpuTexture ProbeDepth
);
