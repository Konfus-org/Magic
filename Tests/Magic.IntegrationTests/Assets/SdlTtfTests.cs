using Magic.Contexts.Assets;
using Magic.Services;
using SDLGem;
using SDLTtfGem;
using Xunit;

namespace Magic.IntegrationTests.Assets;

/// <summary>The font loader over the real SDL3_ttf, rasterising the Arial that ships with Windows.</summary>
[Collection(SdlCollection.Name)]
public sealed class SdlTtfTests : IDisposable
{
    private readonly Sdl _sdl = new(new Project { Name = "Tests" });
    private readonly SdlTtf _ttf = new();

    public void Dispose()
    {
        _ttf.Dispose();
        _sdl.Dispose();
    }

    [Fact]
    public void The_atlas_holds_four_bytes_per_pixel()
    {
        Font font = Load();

        Assert.Equal(font.Width * font.Height * 4, font.Pixels.Length);
    }

    [Fact]
    public void A_printable_glyph_has_a_rectangle_in_the_atlas()
    {
        Font font = Load();

        Glyph glyph = font.Glyphs['A'];

        Assert.True(glyph.Width > 0 && glyph.X + glyph.Width <= font.Width && glyph.Height > 0 && glyph.Y + glyph.Height <= font.Height);
    }

    [Fact]
    public void A_glyphs_rectangle_holds_its_coverage()
    {
        Font font = Load();

        Glyph glyph = font.Glyphs['A'];

        Assert.Contains(font.Pixels.Skip(((glyph.Y * font.Width) + glyph.X) * 4).Take(glyph.Width * 4), pixel => pixel != 0);
    }

    [Fact]
    public void A_printable_glyph_advances_the_pen()
    {
        Font font = Load();

        Glyph glyph = font.Glyphs['A'];

        Assert.True(glyph.Advance > 0);
    }

    private Font Load()
    {
        Font font = new() { Path = "Arial.ttf", Size = 24f };

        _ttf.Load(font, File.ReadAllBytes(@"C:\Windows\Fonts\arial.ttf"));

        return font;
    }
}
