using Magic.Attributes.Assets;

namespace Magic.Contexts.Assets;

/// <summary>
/// One glyph's rectangle in the atlas and its layout metrics, all in pixels.
/// </summary>
/// <param name="BearingX">Left edge of the glyph image relative to the pen position.</param>
/// <param name="BearingY">Top edge of the glyph image above the baseline.</param>
/// <param name="Advance">How far the pen moves after drawing it.</param>
public readonly record struct Glyph(int X, int Y, int Width, int Height, int BearingX, int BearingY, int Advance);

/// <summary>
/// A font rasterised once at <see cref="Size"/>: a white RGBA8 atlas whose alpha is the coverage, and where
/// each codepoint sits in it. A renderer uploads the atlas and draws one quad per glyph.
/// </summary>
public sealed class Font : Asset
{
    /// <summary>
    /// Pixel size the atlas is rasterised at.
    /// </summary>
    [MetaData]
    public float Size { get; set; } = 32f;

    /// <summary>
    /// Distance between baselines.
    /// </summary>
    public int LineHeight { get; set; }

    public int Ascent { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public byte[] Pixels { get; set; } = [];

    /// <summary>
    /// By codepoint.
    /// </summary>
    public Dictionary<uint, Glyph> Glyphs { get; set; } = [];

    public override long Bytes => Pixels.LongLength;
}
