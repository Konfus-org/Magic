using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Magic.Contexts.Assets;
using static Magic.ImGuiDebugUI.ImGui;
using static Magic.ImGuiDebugUI.ImTextureFormat;

namespace Magic.ImGuiDebugUI;

/// <summary>
/// A <see cref="Font"/> asset as ImGui's default font: ImGui asks the loader set up here for each character it first
/// draws at a size, and gets the asset's, scaled from the size it was drawn at. The callbacks find the asset through a
/// handle ImGui keeps as the font's data. Made on the main thread between frames, and disposed once the ImGui context
/// is gone.
/// </summary>
internal sealed unsafe class FontSource : IDisposable
{
    private readonly GCHandle _font;
    private readonly ImFontLoader_t* _loader;
    private readonly nint* _handle;
    private readonly ImFont_t* _added;

    /// <summary>
    /// Adds <paramref name="font"/> to the current context's atlas, as its default font, at <paramref name="size"/>:
    /// the height of a line, ImGui's meaning of a font's size.
    /// </summary>
    internal FontSource(Font font, float size)
    {
        _font = GCHandle.Alloc(font);
        _loader = (ImFontLoader_t*)ImGui_MemAlloc((nuint)sizeof(ImFontLoader_t));
        *_loader = default;
        _loader->FontSrcContainsGlyph = &ContainsGlyph;
        _loader->FontBakedInit = &TryInitBaked;
        _loader->FontBakedLoadGlyph = &TryLoadGlyph;

        // The font's data is the asset's handle, in memory of this object's own: ImGui copies data the atlas does not
        // own, but only once it first reads the font, so the original must outlive this call.
        _handle = (nint*)ImGui_MemAlloc((nuint)sizeof(nint));
        *_handle = GCHandle.ToIntPtr(_font);

        // The C API has no constructor for a config, so the defaults ImGui's sets are set here; the atlas copies it.
        ImFontConfig_t config = default;
        config.ExtraSizeScale = 1f;
        config.GlyphMaxAdvanceX = float.MaxValue;
        config.RasterizerMultiply = 1f;
        config.RasterizerDensity = 1f;
        config.FontLoader = _loader;
        config.FontData = _handle;
        config.FontDataSize = sizeof(nint);
        config.FontDataOwnedByAtlas = false;
        config.SizePixels = size;

        ImGuiIO_t* io = ImGui_GetIO();
        _added = ImFontAtlas_AddFont(io->Fonts, &config);
        io->FontDefault = _added;
    }

    public void Dispose()
    {
        ImGui_MemFree(_loader);
        ImGui_MemFree(_handle);
        _font.Free();
    }

    /// <summary>
    /// Takes the font out of ImGui's atlas, for another to take its place; before <see cref="Dispose"/>, while the
    /// context is still there.
    /// </summary>
    internal void Remove() => ImFontAtlas_RemoveFont(ImGui_GetIO()->Fonts, _added);

    private static Font? GetFont(ImFontConfig_t* source)
        => GCHandle.FromIntPtr(*(nint*)source->FontData).Target as Font;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static bool ContainsGlyph(ImFontAtlas_t* atlas, ImFontConfig_t* source, uint codepoint)
        => GetFont(source) is { } font && font.Glyphs.ContainsKey(codepoint);

    /// <summary>
    /// The font's line, scaled to the size ImGui draws it at.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static bool TryInitBaked(
        ImFontAtlas_t* atlas,
        ImFontConfig_t* source,
        ImFontBaked_t* baked,
        void* loaderData)
    {
        if (GetFont(source) is not { } font) return false;

        float scale = baked->Size / font.LineHeight;
        baked->Ascent = font.Ascent * scale;
        baked->Descent = (font.Ascent - font.LineHeight) * scale;

        return true;
    }

    /// <summary>
    /// One character of the asset into ImGui's atlas, or only how far it moves the pen when that is all ImGui asks for.
    /// Drawn at the asset's size its pixels land one to one; at any other size ImGui stretches them.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static bool TryLoadGlyph(
        ImFontAtlas_t* atlas,
        ImFontConfig_t* source,
        ImFontBaked_t* baked,
        void* loaderData,
        uint codepoint,
        ImFontGlyph_t* glyph,
        float* advance)
    {
        if (GetFont(source) is not { } font || !font.Glyphs.TryGetValue(codepoint, out Glyph loaded)) return false;

        float scale = baked->Size / font.LineHeight;
        if (advance != null)
        {
            *advance = loaded.Advance * scale;

            return true;
        }

        glyph->Codepoint = codepoint;
        glyph->AdvanceX = loaded.Advance * scale;

        if (loaded.Width == 0 || loaded.Height == 0) return true;

        int rectangle = cImFontAtlasPackAddRect(atlas, loaded.Width, loaded.Height);

        if (rectangle < 0) return false;

        // Its box from the pen, scaled to the size drawn: its top is its bearing above the baseline, which is the
        // ascent below the line's top.
        glyph->X0 = loaded.BearingX * scale;
        glyph->Y0 = MathF.Round(baked->Ascent) - (loaded.BearingY * scale);
        glyph->X1 = glyph->X0 + (loaded.Width * scale);
        glyph->Y1 = glyph->Y0 + (loaded.Height * scale);
        glyph->Visible = 1;
        glyph->PackId = rectangle;

        // Its pixels from its top left corner in the asset's image, a row the image's width apart.
        ReadOnlySpan<byte> pixels = font.Pixels.Span[(((loaded.Y * font.Width) + loaded.X) * 4)..];
        fixed (byte* first = pixels)
        {
            cImFontAtlasBakedSetFontGlyphBitmap(
                atlas,
                baked,
                source,
                glyph,
                cImFontAtlasPackGetRect(atlas, rectangle),
                first,
                ImTextureFormat_RGBA32,
                font.Width * 4);
        }

        return true;
    }
}
