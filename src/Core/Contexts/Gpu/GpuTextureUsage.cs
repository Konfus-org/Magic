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

    /// <summary>
    /// Read by vertex and fragment shaders as a storage texture, with no sampler.
    /// </summary>
    GraphicsRead = 1 << 3,

    /// <summary>
    /// Read by compute shaders as a storage texture, with no sampler.
    /// </summary>
    ComputeRead = 1 << 4,

    ComputeWrite = 1 << 5,
}
