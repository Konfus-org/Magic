// What a broken pass looks like on screen: a band along the top of the finished image, the failure magenta
// breathing (Include/Failure.hlsli), with the pass's name across it in the engine's own bitmap font
// (Include/DebugText.hlsli). The renderer draws one band per disabled pass, stacked, over Ldr after the
// posts, with the glyphs of the name in PassRaw[0..6] (one per word, GlyphSpace for anything the font has
// not got) and in PassRaw[7] the band's index (x, the executor's PassIteration), the glyph count (y) and the
// target's size in pixels (zw). Everything outside the band is discarded, so the image under it stays.

#include "Include/Pass.hlsli"
#include "Include/DebugText.hlsli"
#include "Include/Failure.hlsli"

static const uint TextScale = 2u;                                 // texels per glyph texel
static const uint BandHeight = GlyphCell.y * TextScale + 8u;       // the text and a margin
static const uint TextLeft = 8u;
static const uint MaxGlyphs = 28u;                                 // 7 rows of 4 words
static const float3 TextColor = float3(1.0, 1.0, 1.0);

float4 main(PassVaryings input) : SV_Target0
{
    uint band = PassIteration();
    uint2 texel = (uint2)(input.uv * float2(PassRaw[7].zw));
    uint top = band * BandHeight;
    [branch] if (texel.y < top || texel.y >= top + BandHeight)
        discard;

    float3 color = FailureColor * FailurePulse(Time);

    // The glyph this texel lands in, if it is in the text.
    uint2 local = uint2(texel.x - min(texel.x, TextLeft), texel.y - top - 4u) / TextScale;
    uint glyphIndex = local.x / GlyphCell.x;
    uint glyphCount = min(PassRaw[7].y, MaxGlyphs);
    bool isInText = texel.x >= TextLeft && glyphIndex < glyphCount;
    [branch] if (isInText)
    {
        uint4 row = PassRaw[glyphIndex / 4u];
        uint column = glyphIndex % 4u;
        uint glyph = column == 0u ? row.x : column == 1u ? row.y : column == 2u ? row.z : row.w;
        uint2 cell = uint2(local.x % GlyphCell.x, local.y);
        color = GlyphPixel(glyph, cell) ? TextColor : color;
    }

    return float4(color, 1.0);
}
