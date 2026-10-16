using Magic.Contexts.Assets;
using Magic.Contexts.Gpu;

namespace Magic.SdlTextureLoader;

/// <summary>
/// What a texture's <c>.meta</c> holds beside its id: how its image is made into a <see cref="Texture"/>,
/// <c>{ "usage": "normal", "mipmaps": false, "wrap": "clamp" }</c>.
/// </summary>
internal sealed class TextureMeta
{
    public TextureUsage Usage { get; set; } = TextureUsage.Color;

    /// <summary>
    /// Make the mip chain, each level half the one above, down to one texel. Off for an image only ever shown at its
    /// size.
    /// </summary>
    public bool Mipmaps { get; set; } = true;

    /// <summary>
    /// For an <c>.svg</c>: the width, in pixels, it is drawn at, its height kept in proportion; 0 is the size the file
    /// states.
    /// </summary>
    public int Size { get; set; }

    public GpuWrap Wrap { get; set; } = GpuWrap.Repeat;

    public GpuFilter Filter { get; set; } = GpuFilter.Linear;
}

/// <summary>
/// What a texture's pixels are, which picks its colour space.
/// </summary>
internal enum TextureUsage : byte
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
