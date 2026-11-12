// What the resolve draws over a surface whose asset failed, from the material row's failure index and its row of the
// failure table: the flashing colour of the failure's kind, solid where the whole thing failed or in stripes over the
// real surface where a part of it failed, and the failure's text in a 5 by 7 font held here. The text is anchored to
// the instance's origin on the screen, so it is upright and the same size on any mesh.
//
// A row of the failure table, 64 bytes; its C# twin copies this layout:
//   bytes 0 to 3    the header: bits 0 to 7 the kind (FailureKind), bits 8 to 15 the pattern (FailurePattern),
//                   bits 16 to 23 the length of the text in characters, 0 to 56
//   bytes 4 to 59   the text: 56 one-byte character codes, 4 in each uint, the first in the lowest byte
//   bytes 60 to 63  spare
// Row 0 is never read: a failure index of 0 means no failure.
module "Scene/Failures";

import Core.Heap;
import Core.Frame;
import Core.Math;
import Scene.Instances;
import Surface.Surface;

public static const uint GpuFailureBytes = 64u;

// What failed, C#'s twin; the low byte of a row's header.
public enum FailureKind : uint
{
    Model = 0,
    Material = 1,
    Shader = 2,
    Uniform = 3,
    Texture = 4,
}

// How much of the thing failed, C#'s twin: all of it, drawn solid, or a part, drawn in stripes over the real surface.
public enum FailurePattern : uint
{
    Solid = 0,
    Stripes = 1,
}

// The colours of the kinds, linear, from the failure mockup's sRGB colours.
static const float3 Teal = float3(0.012, 0.342, 0.178);
static const float3 Magenta = float3(0.658, 0.087, 0.209);
static const float3 Purple = float3(0.212, 0.184, 0.723);
static const float3 Amber = float3(0.863, 0.347, 0.020);

// The colour of each kind, in FailureKind's order.
static const float3 KindColors[5] = { Teal, Magenta, Purple, Magenta, Amber };

// How often a failure's colour flashes, in pulses a second, and how bright its colour and its text glow.
static const float PulsesPerSecond = 1.5;
static const float ColorGlow = 2.0;
static const float TextGlow = 3.0;

// The width of a stripe and its gap together, in pixels across the diagonal.
static const float StripePeriod = 32.0;

// The render pixels a dot of the font covers, and a character's cell and a text row's height, in dots; and the
// characters of space after each copy of the text.
static const float DotPixels = 2.0;
static const uint CellWidth = 6u;
static const uint RowHeight = 10u;
static const uint CopyGap = 3u;

// The font: printable ASCII from code 32, each character 5 by 7 dots, bit = row * 5 + column from the top left,
// bits 0 to 31 in x and 32 to 34 in y. A 64-bit table draws wrong on the engine's target, so it is two uints.
// FailureBanner.slang holds a copy, as that module imports nothing so that it can be built with no file.
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

// A row of the failure table as the resolve reads it: the table, where the row starts, and its header's fields.
public struct FailureRow
{
    public BufferRef table;
    public uint offset;
    public FailureKind kind;
    public FailurePattern pattern;
    public uint length;
}

// The row of the failure table at a material row's failure index, which is not 0.
public FailureRow LoadFailure(uint table, uint index)
{
    FailureRow failure;
    failure.table.id = table;
    failure.offset = index * GpuFailureBytes;
    uint header = failure.table.Load<uint>(failure.offset);
    failure.kind = (FailureKind)(header & 0xFFu);
    failure.pattern = (FailurePattern)((header >> 8u) & 0xFFu);
    failure.length = (header >> 16u) & 0xFFu;
    return failure;
}

// The code of the failure's character at an index below its length.
uint LoadCode(FailureRow failure, uint index)
{
    uint word = failure.table.Load<uint>(failure.offset + 4u + (index >> 2u) * 4u);
    return (word >> ((index & 3u) * 8u)) & 0xFFu;
}

// Whether a dot of a character's cell, counted from the cell's top left, is set; a code outside the font has none.
public bool IsGlyphDot(uint code, uint2 dot)
{
    if (dot.x >= 5u || dot.y >= 7u || code < FirstCode || code > LastCode)
        return false;

    uint2 glyph = Font[code - FirstCode];
    uint bit = dot.y * 5u + dot.x;
    return (((bit < 32u) ? (glyph.x >> bit) : (glyph.y >> (bit - 32u))) & 1u) != 0u;
}

// Whether a pixel, at an offset in pixels from the anchor, lies on a dot of the failure's text. The text flows in a
// block a number of characters wide, centred on the anchor, its rows repeating down and up, with a gap after each
// copy; a block as wide as a whole copy repeats across too. The dot moves by whole periods to be positive first,
// because signed division and remainder of a negative number drew the rows above the anchor wrong on this target.
bool IsTextPixel(FailureRow failure, float2 offset, uint columns)
{
    uint period = failure.length + CopyGap;
    int2 dot = int2(floor(offset / DotPixels));
    int x = dot.x + int(columns * CellWidth / 2u);
    if (columns == period)
        x += int(period * CellWidth * 4096u);
    else if (x < 0 || x >= int(columns * CellWidth))
        return false;

    // The anchor is 3 dots down the first row, at the middle of its characters' height.
    uint y = uint(dot.y + int(RowHeight * period * 64u) + 3);
    uint column = (uint(x) / CellWidth) % columns;
    uint index = ((y / RowHeight) * columns + column) % period;
    if (index >= failure.length)
        return false;

    return IsGlyphDot(LoadCode(failure, index), uint2(uint(x) % CellWidth, y % RowHeight));
}

// Where a failure's text is anchored, and how it moved: the instance's origin on this frame's jittered screen, in
// texture coordinates, with a block as many characters wide as fit across the instance's bounds on the screen, at
// least one and at most a whole copy; or, where the origin is behind the camera, the screen's top left, with blocks
// a whole copy wide repeating across. The move is the anchor's from where it was last frame, as a pixel's velocity.
struct FailureAnchor
{
    float2 uv;
    uint columns;
    float2 velocity;
}

FailureAnchor GetFailureAnchor(FrameConstants frame, GpuInstance instance, FailureRow failure)
{
    float2 size = float2(frame.width, frame.height);
    float2 jitter = frame.jitter.xy / size;
    uint period = failure.length + CopyGap;
    float4 clip = mul(float4(ToCameraRelative(frame, ToWorld(instance.world, float3(0.0))), 1.0), frame.viewProjection);
    FailureAnchor anchor;
    if (clip.w <= frame.near)
    {
        anchor.uv = jitter;
        anchor.columns = period;
        anchor.velocity = float2(0.0);
        return anchor;
    }

    anchor.uv = ToUv(clip.xy / clip.w);
    float distance = max(length(ToCameraRelative(frame, instance.boundsCenter)), frame.near);
    float across = 2.0 * instance.boundsRadius * frame.pixelsPerRadian / distance;
    anchor.columns = clamp(uint(across / (float(CellWidth) * DotPixels)), 1u, period);
    float3 origin = ToWorld(GetPreviousWorld(instance, frame.frameIndex), float3(0.0)) - frame.previousCameraPosition.xyz;
    anchor.velocity = GetPreviousScreen(frame, origin).xy - (anchor.uv - jitter);
    return anchor;
}

// Draws the failure over a pixel's surface: its kind's colour, pulsing, over all of it or in diagonal stripes, then its
// text, white with a black shadow a pixel down and right. Hands back the pixel's velocity: the anchor's, so the
// history follows the text, not the surface under it.
public float2 ApplyFailure(
    FrameConstants frame,
    uint2 pixel,
    GpuInstance instance,
    FailureRow failure,
    inout SurfaceOutputs surface
)
{
    FailureAnchor anchor = GetFailureAnchor(frame, instance, failure);
    float2 offset = float2(pixel) + 0.5 - anchor.uv * float2(frame.width, frame.height);
    if (failure.pattern == FailurePattern.Solid || frac((offset.x + offset.y) / StripePeriod) < 0.5)
    {
        float pulse = 0.55 + 0.45 * sin(TwoPi * PulsesPerSecond * frame.timeSec);
        float3 color = KindColors[(uint)failure.kind] * pulse;
        surface.baseColor = color;
        surface.emissive = color * ColorGlow;
        surface.metalness = 0.0;
        surface.roughness = 1.0;
        surface.alpha = 1.0;
    }

    if (IsTextPixel(failure, offset, anchor.columns))
    {
        surface.baseColor = float3(1.0);
        surface.emissive = float3(TextGlow);
        surface.alpha = 1.0;
    }
    else if (IsTextPixel(failure, offset - 1.0, anchor.columns))
    {
        surface.baseColor = float3(0.0);
        surface.emissive = float3(0.0);
        surface.alpha = 1.0;
    }

    return anchor.velocity;
}
