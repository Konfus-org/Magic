// Colour: a parameter that is one, luminance, the sRGB and PQ transfer functions, Rec. 2020 from Rec. 709, all on
// linear Rec. 709 (the sRGB primaries) but for what PQ takes; and a target's image encoded as its format expects.

module "Core/Color";

import Core.Noise;

// A parameter that is a colour (r, g, b, a in a .mat file, Color in C#), linear.
public struct Color
{
    float r;
    float g;
    float b;
    float a;

    public property float3 rgb
    {
        get { return float3(r, g, b); }
    }

    public property float4 rgba
    {
        get { return float4(r, g, b, a); }
    }
}

// How a target's image is encoded as it is shown, Core's TargetEncoding: as it is for a render texture, which is
// linear, and for a window whose sRGB-typed image the GPU encodes; sRGB for an SDR window's UNORM image; PQ on
// Rec. 2020 for an HDR10 window's; scRGB, where 1.0 is 80 nits, for an HDR linear window's.
public enum TargetEncoding : uint
{
    Linear,
    Srgb,
    Pq,
    ScRgb,
}

// Nits the finished image's 1.0 shows as on an HDR display: an SDR display's brightest white, about. On an HDR display
// an image tone-mapped to 0..1 shows as it would on an SDR one, its 1.0 at paper white.
public static const float PaperWhiteNits = 200.0;

// The light a linear colour gives off as the eye weighs it (Rec. 709 luma coefficients).
public float GetLuminance(float3 linearRgb)
{
    return dot(linearRgb, float3(0.2126, 0.7152, 0.0722));
}

// The exact sRGB transfer function, both ways: an SDR window's image is UNORM, so the last pass of a frame encodes
// its colour itself.
public float3 DecodeSrgb(float3 encoded)
{
    float3 value = max(encoded, 0.0);
    return select(value <= 0.04045, value / 12.92, pow((value + 0.055) / 1.055, 2.4));
}

public float3 EncodeSrgb(float3 linearRgb)
{
    float3 value = max(linearRgb, 0.0);
    return select(value <= 0.0031308, value * 12.92, 1.055 * pow(value, 1.0 / 2.4) - 0.055);
}

// Rec. 2020's wider primaries from Rec. 709's (ITU-R BT.2087), which HDR10 is composed in.
public static const float3x3 Rec709ToRec2020 = float3x3(
    0.6274040, 0.3292820, 0.0433136,
    0.0690970, 0.9195400, 0.0113612,
    0.0163916, 0.0880132, 0.8955950);

public float3 ToRec2020(float3 linearRgb)
{
    return mul(Rec709ToRec2020, linearRgb);
}

// The ST 2084 (PQ) transfer function, which HDR10 takes: absolute light in nits (cd/m²), up to 10 000, to its
// 10-bit code.
public float3 EncodePq(float3 nits)
{
    const float m1 = 2610.0 / 16384.0;
    const float m2 = 2523.0 / 4096.0 * 128.0;
    const float c1 = 3424.0 / 4096.0;
    const float c2 = 2413.0 / 4096.0 * 32.0;
    const float c3 = 2392.0 / 4096.0 * 32.0;
    float3 y = pow(saturate(nits / 10000.0), m1);
    return pow((c1 + c2 * y) / (1.0 + c3 * y), m2);
}

// A linear colour, tone-mapped to 0..1, encoded for a target: what the last pass that draws into a window writes.
public float3 EncodeForTarget(float3 color, TargetEncoding encoding)
{
    switch (encoding)
    {
    case TargetEncoding.Srgb: return EncodeSrgb(saturate(color));
    case TargetEncoding.Pq: return EncodePq(ToRec2020(saturate(color)) * PaperWhiteNits);
    case TargetEncoding.ScRgb: return saturate(color) * (PaperWhiteNits / 80.0);
    default: return color;
    }
}

// What an encoded colour is dithered by at a pixel: a step of the target's quantisation, 8 bits for sRGB, 10 for PQ,
// so gradients do not band; nothing for a float target.
public float GetTargetDither(TargetEncoding encoding, float2 pixel, uint frame)
{
    switch (encoding)
    {
    case TargetEncoding.Srgb: return GetTriangularDither(pixel, frame) / 255.0;
    case TargetEncoding.Pq: return GetTriangularDither(pixel, frame) / 1023.0;
    default: return 0.0;
    }
}
