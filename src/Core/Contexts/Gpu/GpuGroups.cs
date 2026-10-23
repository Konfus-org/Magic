using System.Drawing;

namespace Magic.Contexts.Gpu;

/// <summary>
/// The groups a dispatch runs, along each axis.
/// </summary>
public readonly record struct GpuGroups(uint X, uint Y = 1, uint Z = 1)
{
    /// <summary>
    /// Groups of <paramref name="tile"/> by <paramref name="tile"/> threads covering <paramref name="size"/>, a thread a
    /// pixel.
    /// </summary>
    public static GpuGroups For(Size size, uint tile)
        => new(((uint)size.Width + tile - 1) / tile, ((uint)size.Height + tile - 1) / tile);

    /// <summary>
    /// Groups of <paramref name="threads"/> covering <paramref name="count"/>, a thread each.
    /// </summary>
    public static GpuGroups For(uint count, uint threads) => new((count + threads - 1) / threads);
}
