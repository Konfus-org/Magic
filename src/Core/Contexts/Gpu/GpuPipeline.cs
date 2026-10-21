using Magic.Interfaces;

namespace Magic.Contexts.Gpu;

/// <summary>
/// A graphics or compute pipeline <see cref="IGpu.CreatePipeline(GpuPipelineDescription)"/> made; which one it is was
/// fixed when it was made.
/// </summary>
public readonly record struct GpuPipeline(uint Id)
{
    /// <summary>
    /// No pipeline.
    /// </summary>
    public static GpuPipeline None => default;
}
