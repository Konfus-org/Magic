// The frame's render failures over the top of the window, one line each: a bar in the flashing colour of the failure's
// kind and its text, white with a black shadow a pixel down and right, in a 5 by 7 font held here, 2 window pixels a
// dot. The module imports nothing, so it builds with no other module: it declares the one heap binding it reads and
// the pass header itself.
//
// The pass header: x the banner's buffer, y the time in seconds as a float's bits, z the lines drawn. The buffer, C#'s
// FailureBanner writes it, holds a line every 128 bytes:
//   bytes 0 to 3    bits 0 to 7 the kind (C#'s RenderFailure), bits 8 to 15 the length of the text in characters
//   bytes 4 to 127  the text: 124 one-byte character codes, 4 in each uint, the first in the lowest byte
module "FailureBanner";

[[vk::binding(0, 0)]] RWByteAddressBuffer bannerBuffers[];

struct BannerHeader
{
    uint buffer;
    uint timeBits;
    uint lines;
    uint unused;
};

[[vk::push_constant]] ConstantBuffer<BannerHeader> banner;

// The most buffers the heap holds: VulkanHeap's constant.
static const uint HeapBuffers = 16384u;

static const uint LineBytes = 128u;

// The window pixels a dot of the font covers; a character's cell and a line's height, in dots; and where the text
// starts in its line, in dots from the line's top left.
static const uint DotPixels = 2u;
static const uint CellWidth = 6u;
static const uint LineHeight = 11u;
static const uint2 TextStart = uint2(4u, 2u);

static const float TwoPi = 6.28318530717959;
static const float PulsesPerSecond = 1.5;

// The colours of the kinds, linear, from the failure mockup's sRGB colours: red #E24B4A, coral #D85A30, green #639922,
// blue #378ADD.
static const float3 Red = float3(0.761, 0.070, 0.068);
static const float3 Coral = float3(0.687, 0.102, 0.030);
static const float3 Green = float3(0.125, 0.319, 0.016);
static const float3 Blue = float3(0.038, 0.254, 0.723);

// The colour of each kind, in RenderFailure's order: Pass, Resource, Shader, Sky, Weather, PostEffect.
static const float3 KindColors[6] = { Red, Red, Red, Coral, Green, Blue };

// The font: printable ASCII from code 32, each character 5 by 7 dots, bit = row * 5 + column from the top left,
// bits 0 to 31 in x and 32 to 34 in y. A copy of Scene/Failures.slang's font: this module imports nothing, so that it
// can be built from the gem's assembly with no file.
static const uint FirstCode = 32u;
static const uint LastCode = 126u;
static const uint2 Font[95] = {
    uint2(0x00000000u, 0u), uint2(0x00421084u, 1u), uint2(0x0000014Au, 0u), uint2(0x95F57D4Au, 2u), //  !"#
    uint2(0x1F4717C4u, 1u), uint2(0x32222263u, 6u), uint2(0x93511526u, 5u), uint2(0x00000084u, 0u), // $%&'
    uint2(0x08210888u, 2u), uint2(0x88842082u, 0u), uint2(0x09575480u, 0u), uint2(0x084F9080u, 0u), // ()*+
    uint2(0x88600000u, 0u), uint2(0x00070000u, 0u), uint2(0x8C000000u, 1u), uint2(0x42222210u, 0u), // ,-./
    uint2(0xA33AE62Eu, 3u), uint2(0x884210C4u, 3u), uint2(0xC444422Eu, 7u), uint2(0xA304111Fu, 3u), // 0123
    uint2(0x11F4A988u, 2u), uint2(0xA3083C3Fu, 3u), uint2(0xA317844Cu, 3u), uint2(0x8422221Fu, 0u), // 4567
    uint2(0xA317462Eu, 3u), uint2(0x910F462Eu, 1u), uint2(0x0C6018C0u, 0u), uint2(0x886018C0u, 0u), // 89:;
    uint2(0x08208888u, 2u), uint2(0x01F07C00u, 0u), uint2(0x88882082u, 0u), uint2(0x0044422Eu, 1u), // <=>?
    uint2(0xAB5B422Eu, 3u), uint2(0x631FC62Eu, 4u), uint2(0xE317C62Fu, 3u), uint2(0xA210862Eu, 3u), // @ABC
    uint2(0xD318C527u, 1u), uint2(0xC217843Fu, 7u), uint2(0x4217843Fu, 0u), uint2(0xA31E862Eu, 7u), // DEFG
    uint2(0x631FC631u, 4u), uint2(0x8842108Eu, 3u), uint2(0x9284211Cu, 1u), uint2(0x52519531u, 4u), // HIJK
    uint2(0xC2108421u, 7u), uint2(0x631AD771u, 4u), uint2(0x639ACE31u, 4u), uint2(0xA318C62Eu, 3u), // LMNO
    uint2(0x4217C62Fu, 0u), uint2(0x9358C62Eu, 5u), uint2(0x5257C62Fu, 4u), uint2(0xE107043Eu, 3u), // PQRS
    uint2(0x0842109Fu, 1u), uint2(0xA318C631u, 3u), uint2(0x1518C631u, 1u), uint2(0xAB5AC631u, 2u), // TUVW
    uint2(0x62A22A31u, 4u), uint2(0x08422A31u, 1u), uint2(0xC222221Fu, 7u), uint2(0x8421084Eu, 3u), // XYZ[
    uint2(0x20820820u, 0u), uint2(0x9084210Eu, 3u), uint2(0x00004544u, 0u), uint2(0xC0000000u, 7u), // backslash ]^_
    uint2(0x00000082u, 0u), uint2(0xA3E83800u, 7u), uint2(0xE319B421u, 3u), uint2(0xA210B800u, 3u), // `abc
    uint2(0xA31CDA10u, 7u), uint2(0x83F8B800u, 3u), uint2(0x84238A4Cu, 0u), uint2(0xA1E8C7C0u, 3u), // defg
    uint2(0x6319B421u, 4u), uint2(0x88421804u, 3u), uint2(0x92843008u, 1u), uint2(0x4A32A421u, 2u), // hijk
    uint2(0x88421086u, 3u), uint2(0x635AAC00u, 4u), uint2(0x6319B400u, 4u), uint2(0xA318B800u, 3u), // lmno
    uint2(0x42F8BC00u, 0u), uint2(0x21ECD800u, 4u), uint2(0x4219B400u, 0u), uint2(0xE0E0B800u, 3u), // pqrs
    uint2(0x24211C42u, 3u), uint2(0xB318C400u, 5u), uint2(0x1518C400u, 1u), uint2(0xAB58C400u, 2u), // tuvw
    uint2(0x54454400u, 4u), uint2(0xA1E8C400u, 3u), uint2(0xC4447C00u, 7u), uint2(0x08411088u, 2u), // xyz{
    uint2(0x08421084u, 1u), uint2(0x88441082u, 0u), uint2(0x008A8800u, 0u),                         // |}~
};

uint LoadWord(uint offset)
{
    return bannerBuffers[NonUniformResourceIndex(min(banner.buffer, HeapBuffers - 1u))].Load(offset);
}

// The header word of the line under a window pixel's row.
uint LoadLine(uint line)
{
    return LoadWord(line * LineBytes);
}

// Whether a window pixel lies on a dot of its line's text; a pixel left of or above the window, or below the lines,
// does not.
bool IsTextPixel(int2 pixel)
{
    if (pixel.x < 0 || pixel.y < 0)
        return false;

    uint2 dot = uint2(pixel) / DotPixels;
    uint line = dot.y / LineHeight;
    if (line >= banner.lines)
        return false;

    uint2 inLine = uint2(dot.x, dot.y % LineHeight);
    if (inLine.x < TextStart.x || inLine.y < TextStart.y)
        return false;

    uint2 inText = inLine - TextStart;
    uint index = inText.x / CellWidth;
    uint2 inCell = uint2(inText.x % CellWidth, inText.y);
    uint length = (LoadLine(line) >> 8u) & 0xFFu;
    if (index >= length || inCell.x >= 5u || inCell.y >= 7u)
        return false;

    uint word = LoadWord(line * LineBytes + 4u + (index >> 2u) * 4u);
    uint code = (word >> ((index & 3u) * 8u)) & 0xFFu;
    if (code < FirstCode || code > LastCode)
        return false;

    uint2 glyph = Font[code - FirstCode];
    uint bit = inCell.y * 5u + inCell.x;
    return (((bit < 32u) ? (glyph.x >> bit) : (glyph.y >> (bit - 32u))) & 1u) != 0u;
}

// One triangle over the whole window; the pass's scissor keeps it to the banner's lines.
[shader("vertex")]
float4 DrawFailureBannerVertex(uint index : SV_VertexID) : SV_Position
{
    float2 corner = float2(float((index << 1u) & 2u), float(index & 2u));
    return float4(corner * 2.0 - 1.0, 0.0, 1.0);
}

[shader("pixel")]
float4 DrawFailureBannerPixel(float4 position : SV_Position) : SV_Target0
{
    int2 pixel = int2(position.xy);
    if (IsTextPixel(pixel))
        return float4(1.0, 1.0, 1.0, 1.0);

    if (IsTextPixel(pixel - 1))
        return float4(0.0, 0.0, 0.0, 1.0);

    uint line = uint(pixel.y) / (DotPixels * LineHeight);
    uint kind = min(LoadLine(line) & 0xFFu, 5u);
    float pulse = 0.55 + 0.45 * sin(TwoPi * PulsesPerSecond * asfloat(banner.timeBits));
    return float4(KindColors[kind] * pulse, 1.0);
}
