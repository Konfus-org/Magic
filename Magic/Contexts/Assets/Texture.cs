using Magic.Attributes.Assets;

namespace Magic.Contexts.Assets;

/// <summary>
/// Pixel format of the stored data. The <c>Srgb</c> variants decode to linear when sampled.
/// </summary>
public enum TextureFormat : byte
{
    Rgba8Unorm,
    Rgba8Srgb,
    Bc7Unorm,
    Bc7Srgb,
    Bc5Unorm,
    Bc4Unorm,
    Rgba16Float
}

/// <summary>
/// What the texture is for. It decides the colour space, the resampling the importer applies and how the
/// renderer binds it: material textures go into pooled arrays, <see cref="Environment"/> and <see cref="Ui"/>
/// are bound on their own at their native size.
/// </summary>
public enum TextureUsage : byte
{
    Color,
    Normal,
    Mask,
    Environment,
    Ui
}

/// <summary>
/// What sampling outside 0..1 reads.
/// </summary>
public enum TextureWrap : byte
{
    Repeat,
    ClampToEdge,
    MirroredRepeat
}

/// <summary>
/// <see cref="Nearest"/> is for pixel art and lookup tables; everything else filters.
/// </summary>
public enum TextureFilter : byte
{
    Linear,
    Nearest
}

/// <summary>
/// The channels of the source image worth keeping: an <see cref="Rgb"/> image still uploads as RGBA, but
/// the importer knows its alpha carries nothing and skips premultiplying.
/// </summary>
public enum TextureChannels : byte
{
    Rgba,
    Rgb
}

/// <summary>
/// One mip level: its size and where its bytes sit in <see cref="Texture.Pixels"/>.
/// </summary>
public readonly record struct TextureLevel(int Width, int Height, int Offset, int Size);

/// <summary>
/// Pixel data ready to upload: every mip level tightly packed one after the other in <see cref="Pixels"/>,
/// level 0 first. The loader has already done colour space, resampling and mip generation, so a renderer
/// copies bytes and nothing else. The <c>[MetaData]</c> properties are import configuration from the sidecar,
/// set before the loader runs.
/// </summary>
public sealed class Texture : Asset
{
    public int Width { get; set; }

    public int Height { get; set; }

    public byte[] Pixels { get; set; } = [];

    public TextureLevel[] Levels { get; set; } = [];

    public TextureFormat Format { get; set; }

    [MetaData]
    public TextureUsage Usage { get; set; }

    /// <summary>
    /// Only honoured for textures bound on their own; pooled material textures share one sampler.
    /// </summary>
    [MetaData]
    public TextureWrap Wrap { get; set; }

    [MetaData]
    public TextureFilter Filter { get; set; }

    /// <summary>
    /// Whether a mip chain was asked for; <see cref="Levels"/> says whether one was produced.
    /// </summary>
    [MetaData]
    public bool Mipmaps { get; set; } = true;

    [MetaData]
    public TextureChannels Channels { get; set; }

    /// <summary>
    /// For a vector image (svg): the pixel size to rasterise it at, or 0 for the size the file states.
    /// </summary>
    [MetaData]
    public int Size { get; set; }

    public override long Bytes => Pixels.LongLength;
}
