using Magic.Interfaces;

namespace Magic.Contexts.Gpu;

/// <summary>
/// A sampler <see cref="IGpuDriver.CreateSampler"/> made.
/// </summary>
public readonly record struct GpuSampler(uint Id)
{
    /// <summary>
    /// No sampler: what a binding of a buffer or a storage texture leaves empty.
    /// </summary>
    public static GpuSampler None => default;
}
