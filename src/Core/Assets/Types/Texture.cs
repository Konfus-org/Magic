using System.Collections.Immutable;

namespace Magic.Assets.Types;

/// <summary>
/// How a <see cref="Texture"/>'s texels are stored. The BC7 formats are a 4 x 4 block of texels in each 16 bytes, a
/// level smaller than a block taking one.
/// </summary>
public enum TextureFormat : byte
{
    /// <summary>
    /// Data such as normals, read as it is.
    /// </summary>
    Rgba8Unorm,

    /// <summary>
    /// Colour, read as linear from sRGB.
    /// </summary>
    Rgba8Srgb,

    Bc7Unorm,
    Bc7Srgb,
}

/// <summary>
/// What a <see cref="Texture"/>'s pixels are, which picks its colour space.
/// </summary>
public enum TextureUsage : byte
{
    /// <summary>
    /// Colour, in sRGB.
    /// </summary>
    Color,

    /// <summary>
    /// A normal map: linear.
    /// </summary>
    Normal,

    /// <summary>
    /// Data, such as roughness, metalness or occlusion: linear.
    /// </summary>
    Mask,
}

/// <summary>
/// How a <see cref="Texture"/> is read between texels.
/// </summary>
public enum TextureFilter : byte
{
    Linear,
    Nearest,
}

/// <summary>
/// What is read past a <see cref="Texture"/>'s edge.
/// </summary>
public enum TextureWrap : byte
{
    /// <summary>
    /// The edge texel.
    /// </summary>
    Clamp,

    /// <summary>
    /// The texture again.
    /// </summary>
    Repeat,

    /// <summary>
    /// The texture again, mirrored every other time.
    /// </summary>
    Mirror,
}

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
    public required int Width { get; init; }

    public required int Height { get; init; }

    public required TextureFormat Format { get; init; }

    public required ReadOnlyMemory<byte> Pixels { get; init; }

    /// <summary>
    /// Where each mip level is in <see cref="Pixels"/>; one when it has no mips.
    /// </summary>
    public required ImmutableArray<TextureLevel> Levels { get; init; }

    /// <summary>
    /// What its pixels are, which picks <see cref="Format"/>'s colour space.
    /// </summary>
    [AssetMeta]
    public TextureUsage Usage { get; set; } = TextureUsage.Color;

    /// <summary>
    /// Whether it has the mip chain, each level half the one above, down to one texel. Off for an image only ever shown
    /// at its size.
    /// </summary>
    [AssetMeta]
    public bool HasMipmaps { get; set; } = true;

    /// <summary>
    /// For an <c>.svg</c>: the width, in pixels, it is drawn at, its height kept in proportion; 0 is the size the file
    /// states.
    /// </summary>
    [AssetMeta]
    public int Size { get; set; }

    /// <summary>
    /// How it is read between texels.
    /// </summary>
    [AssetMeta]
    public TextureFilter Filter { get; set; } = TextureFilter.Linear;

    /// <summary>
    /// What is read past its edge.
    /// </summary>
    [AssetMeta]
    public TextureWrap Wrap { get; set; } = TextureWrap.Repeat;
}
