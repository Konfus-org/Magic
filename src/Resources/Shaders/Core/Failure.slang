// How anything broken looks: magenta, glowing, breathing on a slow sine so it is never mistaken for something meant.
// Magenta is a shader's failure: one that does not compile, a pass that does not run, what only the GPU finds wrong.
// Red is an asset that does not load (Surfaces/Failure, which the failure materials set the kind of). Whatever shows a
// failure draws it this way. What only the GPU knows went wrong, a shader raises here: a code in the frame's Failures
// buffer (RaiseFailure), which the renderer says in words across the view (ApplyFailureBanner), and the pixel it found
// it at drawn as the failure (GetFailureGlow).
//
// The banner is a box of magenta in the view's bottom left, clear of the debug UI's top, breathing as a failure does:
// the passes that do not run and the pipelines that do not load, each wrapped over the rows it takes, then a row for
// each code shaders raised, in a 5 x 7 bitmap font a shader draws itself, at half the size on a view too narrow for a
// full row; nothing when nothing failed. The renderer packs its text (FailureHandler.cs): word 0 is how many rows it
// wrote, then each row's glyphs, FailureLineGlyphs a row, then the words of each code in code order, a row each. A
// glyph is its index in Glyphs: digits from 0, capitals from 10, then an exclamation mark and a space.

module "Core/Failure";

import Core.Heap;
import Core.Math;

public static const float3 FailureColor = float3(1.0, 0.0, 1.0);

// Emissive above 1 so the glow still reads as one after the tonemap.
public static const float FailureGlow = 2.0;

// What only the GPU finds wrong, a word each of the frame's Failures buffer, 1 once raised, emptied every frame. The
// renderer's FailureHandler.cs holds the words for each, in this order: change both together.
public enum FailureCode : uint
{
    NaN,                     // a NaN or infinity in the lit scene
    MaterialRange,           // a material row past the materials there are
}

public static const uint FailureCodeCount = 8u;

// The tonemap flattens a saturated colour above ~0.5, so the pulse's floor sits well below it for the
// breathing to be plain, but never at zero: a failed thing stays visible.
static const float PulseMin = 0.05;
static const float PulseMax = 1.0;
static const float PulsePeriodSec = 2.0;

static const uint FailureLineGlyphs = 96u;

// View pixels a texel of the text covers where a row fits the view at that; else 1.
static const uint BannerScale = 2u;

// Pixels from the view's corner to the box, and from the box to its lines.
static const uint BannerMargin = 8u;
static const float3 BannerText = float3(1.0, 1.0, 1.0);

// A glyph is 35 bits, row after row from the top, each row from the left: bit (row * 5 + column), the first 32 in x
// and the rest in y. It is drawn in a cell one texel wider and taller, so glyphs and lines next to each other stay
// apart.
static const uint2 GlyphSize = uint2(5u, 7u);
static const uint2 GlyphCell = uint2(6u, 8u);
static const uint GlyphSpace = 37u;

static const uint2 Glyphs[38] =
{
    uint2(0xA33AE62Eu, 0x3u), // 0
    uint2(0x884210C4u, 0x3u), // 1
    uint2(0xC444422Eu, 0x7u), // 2
    uint2(0xE107420Fu, 0x3u), // 3
    uint2(0x11F4A988u, 0x2u), // 4
    uint2(0xA3083C3Fu, 0x3u), // 5
    uint2(0xA317844Cu, 0x3u), // 6
    uint2(0x8422221Fu, 0x0u), // 7
    uint2(0xA317462Eu, 0x3u), // 8
    uint2(0x910F462Eu, 0x1u), // 9
    uint2(0x631FC62Eu, 0x4u), // A
    uint2(0xE317C62Fu, 0x3u), // B
    uint2(0xA210862Eu, 0x3u), // C
    uint2(0xE318C62Fu, 0x3u), // D
    uint2(0xC217843Fu, 0x7u), // E
    uint2(0x4217843Fu, 0x0u), // F
    uint2(0xA31E862Eu, 0x7u), // G
    uint2(0x631FC631u, 0x4u), // H
    uint2(0x8842108Eu, 0x3u), // I
    uint2(0x9284211Cu, 0x1u), // J
    uint2(0x52519531u, 0x4u), // K
    uint2(0xC2108421u, 0x7u), // L
    uint2(0x631AD771u, 0x4u), // M
    uint2(0x631CD671u, 0x4u), // N
    uint2(0xA318C62Eu, 0x3u), // O
    uint2(0x4217C62Fu, 0x0u), // P
    uint2(0x9358C62Eu, 0x5u), // Q
    uint2(0x5257C62Fu, 0x4u), // R
    uint2(0xE107043Eu, 0x3u), // S
    uint2(0x0842109Fu, 0x1u), // T
    uint2(0xA318C631u, 0x3u), // U
    uint2(0x1518C631u, 0x1u), // V
    uint2(0x775AC631u, 0x4u), // W
    uint2(0x62A22A31u, 0x4u), // X
    uint2(0x08422A31u, 0x1u), // Y
    uint2(0xC222221Fu, 0x7u), // Z
    uint2(0x00421084u, 0x1u), // !
    uint2(0x00000000u, 0x0u), // space
};

public float GetFailurePulse(float timeSec)
{
    float wave = sin(timeSec * 2.0 * Pi / PulsePeriodSec) * 0.5 + 0.5;
    return lerp(PulseMin, PulseMax, wave);
}

// The light of a pixel shown as the failure: the colour, glowing and breathing.
public float3 GetFailureGlow(float timeSec)
{
    return FailureColor * FailureGlow * GetFailurePulse(timeSec);
}

// Raises code in the frame's Failures buffer, which a compute pass has that lists Failures among its writes.
public void RaiseFailure(ArrayRef<uint> failures, FailureCode code)
{
    failures[(uint)code] = 1u;
}

// Whether the texel of a glyph's cell is ink; the cell's last column and row are always empty.
bool IsGlyphInk(uint glyph, uint2 texel)
{
    uint bit = texel.y * GlyphSize.x + texel.x;
    uint2 rows = Glyphs[min(glyph, GlyphSpace)];
    uint word = bit < 32u ? rows.x : rows.y;
    bool isInGlyph = all(texel < GlyphSize);
    return isInGlyph && ((word >> (bit & 31u)) & 1u) != 0u;
}

// The glyph at column of row, counting the renderer's lines first, then the raised codes' in code order.
uint GetBannerGlyph(ArrayRef<uint> text, ArrayRef<uint> raised, uint written, uint row, uint column)
{
    if (row < written)
    {
        return text[1u + row * FailureLineGlyphs + column];
    }

    uint remaining = row - written;
    [loop] for (uint code = 0u; code < FailureCodeCount; code++)
    {
        if (raised[code] == 0u)
        {
            continue;
        }

        if (remaining == 0u)
        {
            return text[1u + (written + code) * FailureLineGlyphs + column];
        }

        remaining--;
    }

    return GlyphSpace;
}

// color at pixel of the view (from its top left, size across) with the banner over it.
public float3 ApplyFailureBanner(
    float3 color,
    uint2 pixel,
    uint2 size,
    ArrayRef<uint> text,
    ArrayRef<uint> raised,
    float timeSec)
{
    uint written = text[0u];
    uint lines = written;
    [loop] for (uint code = 0u; code < FailureCodeCount; code++)
    {
        lines += raised[code] != 0u ? 1u : 0u;
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

    float3 background = FailureColor * GetFailurePulse(timeSec);
    uint2 inBox = pixel - origin;
    if (any(inBox < BannerMargin) || any(inBox >= BannerMargin + texels * scale))
    {
        return background;
    }

    uint2 texel = (inBox - BannerMargin) / scale;
    uint glyph = GetBannerGlyph(text, raised, written, texel.y / GlyphCell.y, texel.x / GlyphCell.x);
    return IsGlyphInk(glyph, texel % GlyphCell) ? BannerText : background;
}
