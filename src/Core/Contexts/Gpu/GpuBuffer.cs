using Magic.Interfaces;

namespace Magic.Contexts.Gpu;

/// <summary>
/// A buffer <see cref="IGpu.CreateBuffer"/> made.
/// </summary>
public readonly record struct GpuBuffer(uint Id)
{
    /// <summary>
    /// No buffer: what a binding leaves empty.
    /// </summary>
    public static GpuBuffer None => default;
}
