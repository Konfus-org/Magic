namespace Magic.Contexts.Gpu;

/// <summary>
/// An image read back from the GPU: <see cref="Width"/> by <see cref="Height"/> RGBA pixels of 8 bits a channel, rows
/// top to bottom, tightly packed, in the colours a window shows.
/// </summary>
public sealed record GpuImage(int Width, int Height, ReadOnlyMemory<byte> Pixels);
