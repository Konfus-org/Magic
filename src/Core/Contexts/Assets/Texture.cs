using Magic.Contexts.Gpu;
using Magic.Interfaces;

namespace Magic.Contexts.Assets;

/// <summary>
/// One mip level of a <see cref="Texture"/>: its size in texels, and where its bytes are in
/// <see cref="Texture.Pixels"/>.
/// </summary>
public readonly record struct TextureLevel(int Width, int Height, int Offset, int Size);

/// <summary>
/// An image, decoded and ready to upload: every mip level in <see cref="Pixels"/>, level 0 first, tightly packed. Its
/// loader has already turned it into <see cref="Format"/> and made its mips, so whatever draws it copies bytes and
/// nothing else.
/// </summary>
public sealed class Texture : IAsset<Texture>
{
    public static IReadOnlyList<string> Extensions { get; }
        = [".png", ".jpg", ".jpeg", ".bmp", ".tga", ".webp", ".svg"];

    public Handle<Texture> Handle { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>
    /// <see cref="GpuFormat.Rgba8Srgb"/> for colour, <see cref="GpuFormat.Rgba8Unorm"/> for data such as normals.
    /// </summary>
    public required GpuFormat Format { get; init; }

    public required ReadOnlyMemory<byte> Pixels { get; init; }

    /// <summary>
    /// Where each mip level is in <see cref="Pixels"/>; one when it has no mips.
    /// </summary>
    public required IReadOnlyList<TextureLevel> Levels { get; init; }

    /// <summary>
    /// How it is read between texels, as its <c>.meta</c> asks.
    /// </summary>
    public required GpuFilter Filter { get; init; }

    /// <summary>
    /// What is read past its edge, as its <c>.meta</c> asks.
    /// </summary>
    public required GpuWrap Wrap { get; init; }
}
