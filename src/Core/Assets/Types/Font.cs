using System.Collections.Frozen;

namespace Magic.Assets.Types;

/// <summary>
/// One character of a <see cref="Font"/>: its rectangle in the atlas, and where it is drawn from the pen, all in
/// pixels. One that draws nothing, a space, has an empty rectangle and still moves the pen.
/// </summary>
/// <param name="X">The rectangle's left edge in the atlas.</param>
/// <param name="Y">The rectangle's top edge in the atlas.</param>
/// <param name="Width">The rectangle's width.</param>
/// <param name="Height">The rectangle's height.</param>
/// <param name="BearingX">From the pen to the rectangle's left edge.</param>
/// <param name="BearingY">From the baseline up to the rectangle's top edge.</param>
/// <param name="Advance">How far the pen moves after it.</param>
public readonly record struct Glyph(int X, int Y, int Width, int Height, int BearingX, int BearingY, int Advance);

/// <summary>
/// A font, drawn once at <see cref="Size"/>: an atlas of its characters, white RGBA8 whose alpha
/// is coverage, tightly packed, and where each character is in it. Whatever draws text uploads the atlas once and draws
/// a quad per character, moving the pen by its <see cref="Glyph.Advance"/>.
/// </summary>
public sealed class Font : IAsset<Font>
{
    /// <summary>
    /// The pixel size its characters are drawn at, once: the same file at another size is another file.
    /// </summary>
    [AssetMeta]
    public float Size { get; set; } = 32;

    /// <summary>
    /// Pixels from one line's baseline to the next's.
    /// </summary>
    public required int LineHeight { get; init; }

    /// <summary>
    /// Pixels from the baseline to the top of the tallest character.
    /// </summary>
    public required int Ascent { get; init; }

    /// <summary>
    /// The atlas's width, in pixels.
    /// </summary>
    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>
    /// The atlas: <see cref="Width"/> × <see cref="Height"/> RGBA8 pixels, rows top first.
    /// </summary>
    public required ReadOnlyMemory<byte> Pixels { get; init; }

    /// <summary>
    /// Each character it has, by its Unicode code point.
    /// </summary>
    public required FrozenDictionary<uint, Glyph> Glyphs { get; init; }
}
