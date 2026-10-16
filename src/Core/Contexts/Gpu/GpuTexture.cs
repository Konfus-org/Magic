using Magic.Interfaces;

namespace Magic.Contexts.Gpu;

/// <summary>
/// A texture <see cref="IGpu.CreateTexture"/> made, or a window's render target (<see cref="IGpu.WindowTarget"/>).
/// </summary>
public readonly record struct GpuTexture(uint Id)
{
    /// <summary>
    /// No texture: what a binding leaves empty.
    /// </summary>
    public static GpuTexture None => default;
}
