using Magic.Contexts.Assets;
using Magic.Interfaces;
using Magic.Utils;
using SDL3;
using System.Runtime.InteropServices;

namespace SdlFontLoaderGem;

/// <summary>
/// Loads <see cref="Font"/> assets with SDL3_fonts: every glyph of the Latin ranges is rasterised at the
/// sidecar's size and shelf-packed into one RGBA atlas. Loads run one at a time: SDL_fonts fonts are not
/// thread safe, and the library keeps shared state between them.
/// </summary>
internal sealed class SdlFontLoader : IGem, IAssetLoader<Font>
{
    private const int Padding = 1; // between glyphs, so linear sampling never bleeds a neighbour in

    private readonly Lock _loadLock = new();

    public SdlFontLoader()
    {
        if (!TTF.Init())
            throw new InvalidOperationException($"TTF_Init failed: {SDL.GetError()}");
    }

    /// <summary>
    /// Quits the library once the load running on a worker, if any, is done (loads hold the lock); none starts after.
    /// </summary>
    public void Dispose()
    {
        lock (_loadLock)
        {
            Quit = true;
            TTF.Quit();
        }
    }

    /// <summary>
    /// The library has quit: nothing more is loaded.
    /// </summary>
    private bool Quit { get; set; }

    public Result Load(Font asset, byte[] bytes)
    {
        lock (_loadLock)
            return Quit ? Result.Failure("the font loader is gone.") : Rasterise(asset, bytes);
    }

    /// <summary>
    /// Printable ASCII and Latin-1; enough for UI text until a sidecar setting says otherwise.
    /// </summary>
    private static IEnumerable<uint> Codepoints()
    {
        for (uint codepoint = 32; codepoint <= 126; codepoint++)
            yield return codepoint;

        for (uint codepoint = 160; codepoint <= 255; codepoint++)
            yield return codepoint;
    }

    private static Result Rasterise(Font asset, byte[] bytes)
    {
        // The font reads from the bytes for as long as it is open, so they stay pinned until CloseFont.
        GCHandle pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        nint font = 0;
        List<Rasterised> glyphs = [];

        try
        {
            nint io = SDL.IOFromConstMem(pin.AddrOfPinnedObject(), (nuint)bytes.Length);
            if (io == 0)
                return Result.Failure($"SDL_IOFromConstMem failed: {SDL.GetError()}");

            font = TTF.OpenFontIO(io, closeio: true, asset.Size);
            if (font == 0)
                return Result.Failure($"TTF_OpenFont failed: {SDL.GetError()}");

            asset.LineHeight = TTF.GetFontLineSkip(font);
            asset.Ascent = TTF.GetFontAscent(font);

            uint[] wanted = [.. Codepoints().Where(codepoint => TTF.FontHasGlyph(font, codepoint))];
            foreach (uint codepoint in wanted)
            {
                if (!TTF.GetGlyphMetrics(font, codepoint, out int minX, out _, out _, out int maxY, out int advance))
                    continue;

                nint image = TTF.GetGlyphImage(font, codepoint, out TTF.ImageType _);
                if (image == 0)
                {
                    glyphs.Add(new Rasterised(codepoint, [], 0, 0, minX, maxY, advance)); // nothing to draw (a space): still moves the pen
                    continue;
                }

                try
                {
                    nint rgba = SDL.ConvertSurface(image, SDL.PixelFormat.ABGR8888); // R, G, B, A in memory (little endian)
                    if (rgba == 0)
                        return Result.Failure($"SDL_ConvertSurface failed: {SDL.GetError()}");

                    try
                    {
                        glyphs.Add(new Rasterised(codepoint, Copy(rgba, out int width, out int height), width, height, minX, maxY, advance));
                    }
                    finally
                    {
                        SDL.DestroySurface(rgba);
                    }
                }
                finally
                {
                    SDL.DestroySurface(image);
                }
            }
        }
        finally
        {
            if (font != 0)
                TTF.CloseFont(font);

            pin.Free();
        }

        Pack(asset, glyphs);

        return Result.Success();
    }

    /// <summary>
    /// Shelf packing: glyphs go left to right in rows of the tallest glyph, in a power-of-two atlas that
    /// grows until everything fits. Simple and good enough for a few hundred glyphs.
    /// </summary>
    private static void Pack(Font asset, List<Rasterised> glyphs)
    {
        int width = 256;
        int height = 256;
        List<(int X, int Y)> places;
        while (!TryPlace(glyphs, width, height, out places))
        {
            if (width <= height)
                width *= 2;
            else
                height *= 2;
        }

        byte[] atlas = new byte[width * height * 4];
        for (int i = 0; i < glyphs.Count; i++)
        {
            (uint codepoint, byte[] pixels, int glyphWidth, int glyphHeight, int minX, int maxY, int advance) = glyphs[i];
            (int x, int y) = places[i];

            for (int row = 0; row < glyphHeight; row++)
                Buffer.BlockCopy(pixels, row * glyphWidth * 4, atlas, ((y + row) * width + x) * 4, glyphWidth * 4);

            asset.Glyphs[codepoint] = new Glyph(x, y, glyphWidth, glyphHeight, minX, maxY, advance);
        }

        asset.Width = width;
        asset.Height = height;
        asset.Pixels = atlas;
    }

    private static bool TryPlace(List<Rasterised> glyphs, int width, int height, out List<(int X, int Y)> places)
    {
        places = [];
        int x = 0, y = 0, rowHeight = 0;

        foreach ((_, _, int glyphWidth, int glyphHeight, _, _, _) in glyphs)
        {
            if (x + glyphWidth > width)
            {
                x = 0;
                y += rowHeight + Padding;
                rowHeight = 0;
            }

            if (y + glyphHeight > height || glyphWidth > width)
                return false;

            places.Add((x, y));
            x += glyphWidth + Padding;
            rowHeight = Math.Max(rowHeight, glyphHeight);
        }

        return true;
    }

    /// <summary>
    /// The surface's pixels as tightly packed RGBA rows (SDL pads rows to its pitch).
    /// </summary>
    private static byte[] Copy(nint surface, out int width, out int height)
    {
        SDL.Surface info = Marshal.PtrToStructure<SDL.Surface>(surface);
        width = info.Width;
        height = info.Height;
        int rowBytes = width * 4;
        byte[] data = GC.AllocateUninitializedArray<byte>(rowBytes * height);

        bool locked = SDL.LockSurface(surface);
        try
        {
            info = Marshal.PtrToStructure<SDL.Surface>(surface);
            for (int row = 0; row < height; row++)
                Marshal.Copy(info.Pixels + row * info.Pitch, data, row * rowBytes, rowBytes);
        }
        finally
        {
            if (locked)
                SDL.UnlockSurface(surface);
        }

        return data;
    }

    /// <summary>
    /// One glyph as TTF drew it, before it has a place in the atlas.
    /// </summary>
    private readonly record struct Rasterised(uint Codepoint, byte[] Pixels, int Width, int Height, int MinX, int MaxY, int Advance);
}
