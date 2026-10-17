using System.Runtime.InteropServices;
using Magic.Contexts.Assets;

namespace Magic.ImGuiDebugUI;

/// <summary>
/// A <see cref="Font"/> asset as ImGui's default font: ImGui asks the loader set up here for each character it first
/// draws at a size, and gets the asset's, scaled from the size it was drawn at. The callbacks find the asset through a
/// handle ImGui keeps as the font's data. Made on the main thread between frames, and disposed once the ImGui context is
/// gone.
/// </summary>
internal sealed unsafe class FontSource : IDisposable
{
    private readonly GCHandle _font;
    private readonly ImFontLoader* _loader;
    private readonly nint* _handle;
    private readonly ImFont* _added;

    /// <summary>
    /// Adds <paramref name="font"/> to the current context's atlas, as its default font, at <paramref name="size"/>: the
    /// height of a line, ImGui's meaning of a font's size.
    /// </summary>
    public FontSource(Font font, float size)
    {
        _font = GCHandle.Alloc(font);
        _loader = (ImFontLoader*)ImGui.MemAlloc((ulong)sizeof(ImFontLoader));
        *_loader = default;
        _loader->FontSrcContainsGlyph = &ContainsGlyph;
        _loader->FontBakedInit = &InitBaked;
        _loader->FontBakedLoadGlyph = &LoadGlyph;

        // The font's data is the asset's handle, in memory of this object's own: ImGui copies data the atlas does not
        // own, but only once it first reads the font, so the original must outlive this call.
        _handle = (nint*)ImGui.MemAlloc((ulong)sizeof(nint));
        *_handle = GCHandle.ToIntPtr(_font);
        ImFontConfig* config = ImFontConfig.ImFontConfig_();
        config->FontLoader = _loader;
        config->FontData = _handle;
        config->FontDataSize = sizeof(nint);
        config->FontDataOwnedByAtlas = false;
        config->SizePixels = size;

        ImGuiIO* io = ImGui.GetIONil();
        _added = ImFontAtlas.AddFont(io->Fonts, config);
        io->FontDefault = _added;
        ImFontConfig.Destroy(config);
    }

    public void Dispose()
    {
        ImGui.MemFree(_loader);
        ImGui.MemFree(_handle);
        _font.Free();
    }

    /// <summary>
    /// Takes the font out of ImGui's atlas, for another to take its place; before <see cref="Dispose"/>, while the
    /// context is still there.
    /// </summary>
    public void Remove()
    {
        ImFontAtlas.RemoveFont(ImGui.GetIONil()->Fonts, _added);
    }

    private static Font? FontOf(ImFontConfig* source)
    {
        return GCHandle.FromIntPtr(*(nint*)source->FontData).Target as Font;
    }

    [UnmanagedCallersOnly]
    private static bool ContainsGlyph(ImFontAtlas* atlas, ImFontConfig* source, uint codepoint)
    {
        return FontOf(source) is { } font && font.Glyphs.ContainsKey(codepoint);
    }

    /// <summary>
    /// The font's line, scaled to the size ImGui draws it at.
    /// </summary>
    [UnmanagedCallersOnly]
    private static bool InitBaked(ImFontAtlas* atlas, ImFontConfig* source, ImFontBaked* baked, void* loaderData)
    {
        if (FontOf(source) is not { } font)
        {
            return false;
        }

        float scale = baked->Size / font.LineHeight;
        baked->Ascent = font.Ascent * scale;
        baked->Descent = (font.Ascent - font.LineHeight) * scale;
        return true;
    }

    /// <summary>
    /// One character of the asset into ImGui's atlas, or only how far it moves the pen when that is all ImGui asks for.
    /// Drawn at the asset's size its pixels land one to one; at any other size ImGui stretches them.
    /// </summary>
    [UnmanagedCallersOnly]
    private static bool LoadGlyph(ImFontAtlas* atlas, ImFontConfig* source, ImFontBaked* baked, void* loaderData, uint codepoint, ImFontGlyph* glyph, float* advance)
    {
        if (FontOf(source) is not { } font || !font.Glyphs.TryGetValue(codepoint, out Glyph loaded))
        {
            return false;
        }

        float scale = baked->Size / font.LineHeight;
        if (advance != null)
        {
            *advance = loaded.Advance * scale;
            return true;
        }

        glyph->Codepoint = codepoint;
        glyph->AdvanceX = loaded.Advance * scale;
        if (loaded.Width == 0 || loaded.Height == 0)
        {
            return true;
        }

        int rectangle = ImGui.ImFontAtlasPackAddRect(atlas, loaded.Width, loaded.Height, null);
        if (rectangle < 0)
        {
            return false;
        }

        glyph->X0 = loaded.BearingX * scale;
        glyph->Y0 = MathF.Round(baked->Ascent) - (loaded.BearingY * scale);
        glyph->X1 = glyph->X0 + (loaded.Width * scale);
        glyph->Y1 = glyph->Y0 + (loaded.Height * scale);
        glyph->Visible = 1;
        glyph->PackId = rectangle;

        ReadOnlySpan<byte> pixels = font.Pixels.Span[(((loaded.Y * font.Width) + loaded.X) * 4)..];
        fixed (byte* first = pixels)
        {
            ImGui.ImFontAtlasBakedSetFontGlyphBitmap(atlas, baked, source, glyph, ImGui.ImFontAtlasPackGetRect(atlas, rectangle), first, ImTextureFormat.Rgba32, font.Width * 4);
        }

        return true;
    }
}
