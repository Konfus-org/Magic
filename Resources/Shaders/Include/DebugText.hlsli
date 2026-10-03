// Text a shader draws itself, for what only the GPU knows went wrong: a 5 x 7 bitmap font (digits, capitals,
// space) and 3 x 5 digits, as bits in constants. No texture and no font asset, so any fragment or compute
// shader can include it; the cost is a few bit tests per pixel, so keep it inside the branch that needs it.
//
// A glyph is 35 bits, row after row from the top, each row from the left: bit (row * 5 + column), the first
// 32 in x and the rest in y. It is drawn in a cell one texel wider and taller, so glyphs next to each other
// and lines under each other stay apart. A caller that writes a message keeps it as an array of glyph
// indices: GlyphDigit0 + n, GlyphLetterA + n, GlyphSpace.

#ifndef MAGIC_DEBUG_TEXT_HLSLI
#define MAGIC_DEBUG_TEXT_HLSLI

static const uint2 GlyphSize = uint2(5u, 7u);
static const uint2 GlyphCell = uint2(6u, 8u);
static const uint GlyphDigit0 = 0u;
static const uint GlyphLetterA = 10u;
static const uint GlyphSpace = 36u;

static const uint2 SmallDigitSize = uint2(3u, 5u);
static const uint SmallDigitAdvance = 4u; // a digit and the gap after it

static const uint2 Glyphs[37] =
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
    uint2(0x00000000u, 0x0u), // space
};

// 15 bits each, bit (row * 3 + column).
static const uint SmallDigits[10] = { 0x7B6Fu, 0x749Au, 0x73E7u, 0x79E7u, 0x49EDu, 0x79CFu, 0x7BCFu, 0x24A7u, 0x7BEFu, 0x79EFu };

// Whether the texel of a glyph's cell is ink; the cell's last column and row are always empty.
bool GlyphPixel(uint glyph, uint2 texel)
{
    uint bit = texel.y * GlyphSize.x + texel.x;
    uint2 rows = Glyphs[min(glyph, GlyphSpace)];
    uint word = bit < 32u ? rows.x : rows.y;
    bool isInGlyph = all(texel < GlyphSize);
    return isInGlyph && ((word >> (bit & 31u)) & 1u) != 0u;
}

// Whether the texel is ink of the number written in small digits, digitCount of them from the left with
// leading zeros, each SmallDigitAdvance wide: texel (0, 0) is the top left of the first digit.
bool SmallNumberPixel(uint value, uint digitCount, uint2 texel)
{
    uint place = texel.x / SmallDigitAdvance;
    uint column = texel.x % SmallDigitAdvance;

    // The digit at this place: divide away the places to its right.
    uint shifted = value;
    [loop] for (uint right = place + 1u; right < digitCount; right++)
        shifted /= 10u;

    uint bit = texel.y * SmallDigitSize.x + column;
    bool isInDigit = place < digitCount && column < SmallDigitSize.x && texel.y < SmallDigitSize.y;
    return isInDigit && ((SmallDigits[shifted % 10u] >> bit) & 1u) != 0u;
}

#endif
