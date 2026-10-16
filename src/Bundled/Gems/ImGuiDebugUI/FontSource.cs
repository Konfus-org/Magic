using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Hexa.NET.ImGui;
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
    private readonly ImFontPtr _added;

    /// <summary>
    /// Adds <paramref name="font"/> to the current context's atlas, as its default font, at <paramref name="size"/>: the
    /// height of a line, ImGui's meaning of a font's size.
    /// </summary>
    public FontSource(Font font, float size)
    {
        _font = GCHandle.Alloc(font);
        _loader = (ImFontLoader*)ImGui.MemAlloc((nuint)sizeof(ImFontLoader));
        *_loader = default;
        _loader->FontSrcContainsGlyph = (delegate* unmanaged[Cdecl]<ImFontAtlas*, ImFontConfig*, uint, byte>)&ContainsGlyph;
        _loader->FontBakedInit = (delegate* unmanaged[Cdecl]<ImFontAtlas*, ImFontConfig*, ImFontBaked*, void*, byte>)&InitBaked;
        _loader->FontBakedLoadGlyph = (delegate* unmanaged[Cdecl]<ImFontAtlas*, ImFontConfig*, ImFontBaked*, void*, uint, ImFontGlyph*, float*, byte>)&LoadGlyph;

        // ImGui copies a font's data, here the asset's handle, as the atlas does not own it.
        nint handle = GCHandle.ToIntPtr(_font);
        ImFontConfigPtr config = ImGui.ImFontConfig();
        config.FontLoader = _loader;
        config.FontData = &handle;
        config.FontDataSize = sizeof(nint);
        config.FontDataOwnedByAtlas = false;
        config.SizePixels = size;

        ImGuiIOPtr io = ImGui.GetIO();
        _added = io.Fonts.AddFont(config);
        io.FontDefault = _added;
        config.Destroy();
    }

    public void Dispose()
    {
        ImGui.MemFree(_loader);
        _font.Free();
    }

    /// <summary>
    /// Takes the font out of ImGui's atlas, for another to take its place; before <see cref="Dispose"/>, while the
    /// context is still there.
    /// </summary>
    public void Remove()
    {
        ImGui.GetIO().Fonts.RemoveFont(_added);
    }

    private static Font? FontOf(ImFontConfig* source)
    {
        return GCHandle.FromIntPtr(*(nint*)source->FontData).Target as Font;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte ContainsGlyph(ImFontAtlas* atlas, ImFontConfig* source, uint codepoint)
    {
        return (byte)(FontOf(source) is { } font && font.Glyphs.ContainsKey(codepoint) ? 1 : 0);
    }

    /// <summary>
    /// The font's line, scaled to the size ImGui draws it at.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte InitBaked(ImFontAtlas* atlas, ImFontConfig* source, ImFontBaked* baked, void* loaderData)
    {
        if (FontOf(source) is not { } font)
        {
            return 0;
        }

        float scale = baked->Size / font.LineHeight;
        baked->Ascent = font.Ascent * scale;
        baked->Descent = (font.Ascent - font.LineHeight) * scale;
        return 1;
    }

    /// <summary>
    /// One character of the asset into ImGui's atlas, or only how far it moves the pen when that is all ImGui asks for.
    /// Drawn at the asset's size its pixels land one to one; at any other size ImGui stretches them.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte LoadGlyph(ImFontAtlas* atlas, ImFontConfig* source, ImFontBaked* baked, void* loaderData, uint codepoint, ImFontGlyph* glyph, float* advance)
    {
        if (FontOf(source) is not { } font || !font.Glyphs.TryGetValue(codepoint, out Glyph loaded))
        {
            return 0;
        }

        float scale = baked->Size / font.LineHeight;
        if (advance != null)
        {
            *advance = loaded.Advance * scale;
            return 1;
        }

        glyph->Codepoint = codepoint;
        glyph->AdvanceX = loaded.Advance * scale;
        if (loaded.Width == 0 || loaded.Height == 0)
        {
            return 1;
        }

        int rectangle = ImGuiP.ImFontAtlasPackAddRect(atlas, loaded.Width, loaded.Height);
        if (rectangle < 0)
        {
            return 0;
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
            ImGuiP.ImFontAtlasBakedSetFontGlyphBitmap(atlas, baked, source, glyph, ImGuiP.ImFontAtlasPackGetRect(atlas, rectangle), first, ImTextureFormat.Rgba32, font.Width * 4);
        }

        return 1;
    }
}
