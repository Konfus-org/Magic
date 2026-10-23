namespace Magic.Contexts.Gpu;

/// <summary>
/// What a texture may be used for, fixed when it is made.
/// </summary>
[Flags]
public enum GpuTextureUsage : byte
{
    None = 0,

    /// <summary>
    /// Sampled, through a sampler, by any stage.
    /// </summary>
    Sampled = 1 << 0,

    ColorTarget = 1 << 1,
    DepthTarget = 1 << 2,

    ComputeWrite = 1 << 3,
}
