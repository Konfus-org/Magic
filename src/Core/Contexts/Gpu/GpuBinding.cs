namespace Magic.Contexts.Gpu;

/// <summary>
/// One thing a command binds: a buffer, or a texture, with its sampler when it is sampled. <see cref="Level"/> and
/// <see cref="Layer"/> pick the mip level and layer (or 3D slice, or cube face) a render pass draws into or a compute
/// pass writes.
/// </summary>
public readonly record struct GpuBinding(
    GpuBuffer Buffer = default,
    GpuTexture Texture = default,
    GpuSampler Sampler = default,
    uint Level = 0,
    uint Layer = 0);
