namespace Magic.Graphics;

/// <summary>
/// The descriptions a gem's own buffers and textures are asked for by, each under its name.
/// </summary>
public static class GpuDescriptions
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
