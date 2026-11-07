using Magic.Gpu;

namespace Magic.GIRendering;

/// <summary>
/// The descriptions the gem's own buffers and textures are asked for by, each under its name.
/// </summary>
internal static class GpuDescriptions
{
    public static BufferDescription GetBuffer(string name, long bytes, BufferUsages usage = BufferUsages.Storage) =>
        new()
        {
            Bytes = bytes,
            Usage = usage,
            Name = name,
        };

    public static TextureDescription GetTexture(
        string name,
        int width,
        int height,
        GpuFormat format,
        TextureUsages usage = TextureUsages.Storage | TextureUsages.Sampled,
        int levels = 1
    ) =>
        new()
        {
            Width = width,
            Height = height,
            Format = format,
            Usage = usage,
            Levels = levels,
            Name = name,
        };
}
