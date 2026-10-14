using Magic.Contexts.Assets;

namespace Magic.SdlFontLoader;

/// <summary>
/// What a font's <c>.meta</c> holds beside its id: the size its <see cref="Font"/> is drawn at, <c>{ "size": 13 }</c>.
/// </summary>
internal sealed class FontMeta
{
    /// <summary>
    /// The pixel size its characters are drawn at, once: the same file at another size is another file.
    /// </summary>
    public float Size { get; set; } = 32;
}
