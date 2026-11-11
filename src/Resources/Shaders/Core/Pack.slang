// Values as the GBuffer and the tables keep them: unit vectors folded onto a square, and small numbers packed into
// words, each with the decode beside its encode.
module "Core/Pack";

// A unit vector as a point in [-1, 1]², the octahedron folded flat, which keeps the precision even over the sphere
// (Cigolle et al., "A Survey of Efficient Representations for Independent Unit Vectors", 2014).
public float2 EncodeOctahedral(float3 unit)
{
    float3 n = unit / (abs(unit.x) + abs(unit.y) + abs(unit.z));
    float2 flat = n.xy;
    if (n.z < 0.0)
        flat = (1.0 - abs(n.yx)) * float2(n.x >= 0.0 ? 1.0 : -1.0, n.y >= 0.0 ? 1.0 : -1.0);

    return flat;
}

public float3 DecodeOctahedral(float2 flat)
{
    float3 n = float3(flat.x, flat.y, 1.0 - abs(flat.x) - abs(flat.y));
    float t = saturate(-n.z);
    n.x += n.x >= 0.0 ? -t : t;
    n.y += n.y >= 0.0 ? -t : t;
    return normalize(n);
}

// Two numbers in [0, 1] as 16 bits each of one word.
public uint PackUnorm2x16(float2 v)
{
    uint2 bits = uint2(round(saturate(v) * 65535.0));
    return bits.x | (bits.y << 16u);
}

public float2 UnpackUnorm2x16(uint packed)
{
    return float2(packed & 0xFFFFu, packed >> 16u) / 65535.0;
}

// Four numbers in [0, 1] as a byte each of one word, x in the low byte.
public uint PackUnorm4x8(float4 v)
{
    uint4 bits = uint4(round(saturate(v) * 255.0));
    return bits.x | (bits.y << 8u) | (bits.z << 16u) | (bits.w << 24u);
}

public float4 UnpackUnorm4x8(uint packed)
{
    return float4(packed & 0xFFu, (packed >> 8u) & 0xFFu, (packed >> 16u) & 0xFFu, packed >> 24u) / 255.0;
}

// A unit vector as its octahedral point moved to [0, 1]², as the GBuffer's normal texture keeps it.
public float2 EncodeUnitVectorUnorm(float3 unit) { return EncodeOctahedral(unit) * 0.5 + 0.5; }

public float3 DecodeUnitVectorUnorm(float2 unorm) { return DecodeOctahedral(unorm * 2.0 - 1.0); }

// A unit vector as one word: its octahedral point at 16 bits an axis, which a normal buffer and a cluster's cone keep.
public uint EncodeUnitVector(float3 unit) { return PackUnorm2x16(EncodeUnitVectorUnorm(unit)); }

public float3 DecodeUnitVector(uint packed) { return DecodeUnitVectorUnorm(UnpackUnorm2x16(packed)); }

// A number in [-1, 1] from a signed 16-bit fraction in the low half of a word, as a cluster's cone cutoff is kept.
public float UnpackSnorm16(uint packed)
{
    int bits = int(packed & 0xFFFFu);
    if (bits >= 32768)
        bits -= 65536;

    return max(float(bits) / 32767.0, -1.0);
}

// A number of 0 or more as the bits of a small float: a 5-bit exponent over a mantissa of mantissaBits, rounded to the
// nearest from the 32-bit float's own bits, so no 16-bit type is needed. Under 2^-14 is 0, and the format's largest
// finite value the most; a float of 0 or more orders as its bits do, so the bits' min is the value's.
uint PackSmallFloat(float value, uint mantissaBits)
{
    uint largest = ((30u + 112u) << 23u) | (((1u << mantissaBits) - 1u) << (23u - mantissaBits));
    uint bits = min(asuint(max(value, 0.0)) + (1u << (22u - mantissaBits)), largest);
    int exponent = int((bits >> 23u) & 0xFFu) - 112;
    if (!(value > 0.0) || exponent <= 0)
        return 0u;

    return (uint(exponent) << mantissaBits) | ((bits >> (23u - mantissaBits)) & ((1u << mantissaBits) - 1u));
}

float UnpackSmallFloat(uint packed, uint mantissaBits)
{
    uint exponent = packed >> mantissaBits;
    uint mantissa = packed & ((1u << mantissaBits) - 1u);
    return exponent == 0u ? 0.0 : asfloat(((exponent + 112u) << 23u) | (mantissa << (23u - mantissaBits)));
}

// A number as a 16-bit float in the low half of a word.
public uint PackHalf(float value) { return (value < 0.0 ? 0x8000u : 0u) | PackSmallFloat(abs(value), 10u); }

public float UnpackHalf(uint packed)
{
    float magnitude = UnpackSmallFloat(packed & 0x7FFFu, 10u);
    return (packed & 0x8000u) != 0u ? -magnitude : magnitude;
}

// A colour of 0 or more as one word: 11, 11 and 10-bit floats, red in the low bits.
public uint PackR11G11B10(float3 color)
{
    return PackSmallFloat(color.r, 6u) | (PackSmallFloat(color.g, 6u) << 11u) | (PackSmallFloat(color.b, 5u) << 22u);
}

public float3 UnpackR11G11B10(uint packed)
{
    return float3(
        UnpackSmallFloat(packed & 0x7FFu, 6u),
        UnpackSmallFloat((packed >> 11u) & 0x7FFu, 6u),
        UnpackSmallFloat(packed >> 22u, 5u)
    );
}
