// The words of what failed this frame, drawn over a view's finished image by whatever shows it: a box of magenta in
// the view's bottom left, clear of the debug UI's top, breathing as a failure does, the passes that do not run and the
// pipelines that do not load, each wrapped over the rows it takes, then a row for each code shaders raised
// (Include/Failure.hlsli), in the text a shader draws itself (Include/DebugText.hlsli), at half the size on a view
// too narrow for a full row. Nothing when nothing failed.
//
// The renderer packs FailureText (FailureHandler.cs): word 0 is how many rows it wrote, then each row's glyphs,
// FailureLineGlyphs a row, then the words of each code in code order, a row each. Failures holds a word per code,
// 1 once raised.

#ifndef MAGIC_FAILURE_BANNER_HLSLI
#define MAGIC_FAILURE_BANNER_HLSLI

#include "Include/DebugText.hlsli"
#include "Include/Failure.hlsli"

static const uint FailureLineGlyphs = 96u;

// View pixels a texel of the text covers where a row fits the view at that; else 1.
static const uint BannerScale = 2u;

// Pixels from the view's corner to the box, and from the box to its lines.
static const uint BannerMargin = 8u;
static const float3 BannerText = float3(1.0, 1.0, 1.0);

// The glyph at column of row, counting the renderer's lines first, then the raised codes' in code order. The buffers are
// byte-addressed, read-only or not.
template<typename TBuffer>
uint BannerGlyph(TBuffer text, TBuffer raised, uint written, uint row, uint column)
{
    if (row < written)
    {
        return text.Load((1u + row * FailureLineGlyphs + column) * 4u);
    }

    uint remaining = row - written;
    [loop] for (uint code = 0u; code < FailureCodeCount; code++)
    {
        if (raised.Load(code * 4u) == 0u)
        {
            continue;
        }

        if (remaining == 0u)
        {
            return text.Load((1u + (written + code) * FailureLineGlyphs + column) * 4u);
        }

        remaining--;
    }

    return GlyphSpace;
}

// color at pixel of the view (from its top left, size across) with the banner over it.
template<typename TBuffer>
float3 FailureBanner(float3 color, uint2 pixel, uint2 size, TBuffer text, TBuffer raised, float time)
{
    uint written = text.Load(0u);
    uint lines = written;
    [loop] for (uint code = 0u; code < FailureCodeCount; code++)
    {
        lines += raised.Load(code * 4u) != 0u ? 1u : 0u;
    }

    [branch] if (lines == 0u)
    {
        return color;
    }

    uint2 texels = uint2(FailureLineGlyphs * GlyphCell.x, lines * GlyphCell.y);
    uint scale = texels.x * BannerScale + 4u * BannerMargin <= size.x ? BannerScale : 1u;
    uint2 box = texels * scale + 2u * BannerMargin;
    uint2 origin = uint2(BannerMargin, size.y - min(size.y, box.y + BannerMargin));
    if (any(pixel < origin) || any(pixel >= origin + box))
    {
        return color;
    }

    float3 background = FailureColor * FailurePulse(time);
    uint2 inBox = pixel - origin;
    if (any(inBox < BannerMargin) || any(inBox >= BannerMargin + texels * scale))
    {
        return background;
    }

    uint2 texel = (inBox - BannerMargin) / scale;
    uint glyph = BannerGlyph(text, raised, written, texel.y / GlyphCell.y, texel.x / GlyphCell.x);
    bool isInk = GlyphPixel(glyph, texel % GlyphCell);
    return isInk ? BannerText : background;
}

#endif
