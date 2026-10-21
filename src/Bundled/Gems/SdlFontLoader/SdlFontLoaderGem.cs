using System.Collections.Frozen;
using System.Text.Json;
using Magic.Contexts.Assets;
using Magic.Contexts.Utils;
using Magic.Interfaces;
using Magic.SdlBindings;
using Magic.Services;

namespace Magic.SdlFontLoader;

/// <summary>
/// Reads fonts into <see cref="Font"/>s with SDL_ttf: every character of printable ASCII and Latin-1 the font has is
/// drawn at the size its <c>.meta</c> asks for (<see cref="FontMeta"/>), and shelf-packed into one atlas. SDL_ttf
/// shares state between its fonts, so files load one at a time.
/// </summary>
internal sealed unsafe class SdlFontLoaderGem : IGem, IAssetLoader<Font>, IDisposable
{
    /// <summary>
    /// Pixels between characters in the atlas, so linear sampling never bleeds a neighbour in.
    /// </summary>
    private const int Padding = 1;

    /// <summary>
    /// Held while a file loads, and while SDL_ttf stops.
    /// </summary>
    private readonly Lock _loading = new();

    /// <summary>
    /// SDL_ttf has stopped: nothing more loads.
    /// </summary>
    private bool _stopped;

    public SdlFontLoaderGem()
    {
        // SDL itself first, so SDL_ttf binds to the SDL every gem shares.
        _ = SDL.GetVersion();
        if (!TTF.Init())
        {
            throw new InvalidOperationException($"SDL_ttf could not start: {CString.Read(SDL.GetError())}");
        }
    }

    /// <summary>
    /// Stops SDL_ttf once the file loading on a worker, if any, is done; none loads after.
    /// </summary>
    public void Dispose()
    {
        lock (_loading)
        {
            _stopped = true;
            TTF.Quit();
        }
    }

    public ValueTask<Result<Font>> LoadAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        JsonElement settings,
        CancellationToken cancel)
    {
        return new(Load(bytes.Span, settings));
    }

    /// <summary>
    /// The characters of the font in <paramref name="file"/>, drawn <paramref name="size"/> pixels high and packed into
    /// an atlas. Under <see cref="_loading"/>.
    /// </summary>
    private static Result<Font> Draw(ReadOnlySpan<byte> file, float size)
    {
        // SDL_ttf reads the file for as long as the font is open, so it stays pinned until then.
        fixed (byte* bytes = file)
        {
            SDL.IOStream* io = SDL.IOFromConstMem(bytes, (ulong)file.Length);
            TTF.Font* font = io == null ? null : TTF.OpenFontIO(io, closeio: true, size);
            if (font == null) return Result.Failure<Font>($"SDL_ttf could not read it: {CString.Read(SDL.GetError())}");

            try
            {
                List<Drawn> drawn = [];
                for (uint codepoint = 32; codepoint <= 255; codepoint++)
                {
                    // Printable ASCII, then Latin-1 past its control characters.
                    if (codepoint is > 126 and < 160 || !TTF.FontHasGlyph(font, codepoint)) continue;

                    Result<Drawn> glyph = DrawGlyph(font, codepoint);
                    if (!glyph.Ok) return Result.Failure<Font>(glyph.Error);

                    drawn.Add(glyph.Value);
                }

                return Result.Success(Pack(size, TTF.GetFontLineSkip(font), TTF.GetFontAscent(font), drawn));
            }
            finally
            {
                TTF.CloseFont(font);
            }
        }
    }

    /// <summary>
    /// <paramref name="codepoint"/>'s image as tightly packed RGBA8 rows, white with its coverage in alpha, and where
    /// it goes from the pen. A character that draws nothing, a space, has no pixels and still moves the pen.
    /// </summary>
    private static Result<Drawn> DrawGlyph(TTF.Font* font, uint codepoint)
    {
        int minX;
        int maxX;
        int minY;
        int maxY;
        int advance;
        if (!TTF.GetGlyphMetrics(font, codepoint, &minX, &maxX, &minY, &maxY, &advance))
        {
            return Result.Failure<Drawn>($"SDL_ttf could not measure U+{codepoint:X4}: {CString.Read(SDL.GetError())}");
        }

        TTF.ImageType type;
        SDL.Surface* image = TTF.GetGlyphImage(font, codepoint, &type);
        if (image == null || image->W == 0 || image->H == 0)
        {
            if (image != null) SDL.DestroySurface(image);

            return Result.Success(new Drawn(codepoint, [], 0, 0, minX, maxY, advance));
        }

        // R, G, B, A bytes in memory whatever the machine's byte order.
        SDL.Surface* rgba = SDL.ConvertSurface(image, SDL.PixelFormat.Rgba32);
        SDL.DestroySurface(image);
        if (rgba == null)
        {
            return Result.Failure<Drawn>(
                $"SDL could not convert U+{codepoint:X4} to RGBA8: {CString.Read(SDL.GetError())}");
        }

        int row = rgba->W * 4;
        byte[] pixels = GC.AllocateUninitializedArray<byte>(row * rgba->H);
        for (int y = 0; y < rgba->H; y++)
        {
            new ReadOnlySpan<byte>((byte*)rgba->Pixels + (y * rgba->Pitch), row).CopyTo(pixels.AsSpan(y * row, row));
        }

        Drawn drawn = new(codepoint, pixels, rgba->W, rgba->H, minX, maxY, advance);
        SDL.DestroySurface(rgba);
        return Result.Success(drawn);
    }

    /// <summary>
    /// <paramref name="drawn"/> packed into the smallest atlas, a power of two on each side from 256, that holds them.
    /// </summary>
    private static Font Pack(float size, int lineHeight, int ascent, List<Drawn> drawn)
    {
        int width = 256;
        int height = 256;
        List<(int X, int Y)> places = [];
        while (!TryPlace(drawn, width, height, places))
        {
            if (width <= height)
            {
                width *= 2;
            }
            else
            {
                height *= 2;
            }
        }

        byte[] atlas = new byte[width * height * 4];
        Dictionary<uint, Glyph> glyphs = new(drawn.Count);
        for (int i = 0; i < drawn.Count; i++)
        {
            Drawn glyph = drawn[i];
            (int x, int y) = places[i];
            int row = glyph.Width * 4;
            for (int line = 0; line < glyph.Height; line++)
            {
                glyph.Pixels.AsSpan(line * row, row).CopyTo(atlas.AsSpan((((y + line) * width) + x) * 4, row));
            }

            glyphs[glyph.Codepoint]
                = new Glyph(x, y, glyph.Width, glyph.Height, glyph.BearingX, glyph.BearingY, glyph.Advance);
        }

        return new Font
        {
            Size = size,
            LineHeight = lineHeight,
            Ascent = ascent,
            Width = width,
            Height = height,
            Pixels = atlas,
            Glyphs = glyphs.ToFrozenDictionary(),
        };
    }

    /// <summary>
    /// Shelf packing, into <paramref name="places"/>: characters left to right, in rows as tall as the tallest in each;
    /// false when they do not all fit in <paramref name="width"/> × <paramref name="height"/>.
    /// </summary>
    private static bool TryPlace(List<Drawn> drawn, int width, int height, List<(int X, int Y)> places)
    {
        places.Clear();
        int x = 0;
        int y = 0;
        int rowHeight = 0;
        foreach (Drawn glyph in drawn)
        {
            if (x + glyph.Width > width)
            {
                x = 0;
                y += rowHeight + Padding;
                rowHeight = 0;
            }

            if (glyph.Width > width || y + glyph.Height > height) return false;

            places.Add((x, y));
            x += glyph.Width + Padding;
            rowHeight = Math.Max(rowHeight, glyph.Height);
        }

        return true;
    }

    private Result<Font> Load(ReadOnlySpan<byte> bytes, JsonElement settings)
    {
        Result<FontMeta> meta = Json.Read<FontMeta>(settings);
        if (!meta.Ok) return Result.Failure<Font>($"its .meta is not a font's: {meta.Error}");

        lock (_loading)
        {
            return _stopped ? Result.Failure<Font>("the font loader has stopped.") : Draw(bytes, meta.Value.Size);
        }
    }

    /// <summary>
    /// One character as SDL_ttf drew it, before it has a place in the atlas.
    /// </summary>
    private readonly record struct Drawn(
        uint Codepoint,
        byte[] Pixels,
        int Width,
        int Height,
        int BearingX,
        int BearingY,
        int Advance);
}
