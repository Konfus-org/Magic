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

// A unit vector as one word: its octahedral point at 16 bits an axis, which a normal buffer and a cluster's cone keep.
public uint EncodeUnitVector(float3 unit) { return PackUnorm2x16(EncodeOctahedral(unit) * 0.5 + 0.5); }

public float3 DecodeUnitVector(uint packed) { return DecodeOctahedral(UnpackUnorm2x16(packed) * 2.0 - 1.0); }

// A number in [-1, 1] from a signed 16-bit fraction in the low half of a word, as a cluster's cone cutoff is kept.
public float UnpackSnorm16(uint packed)
{
    int bits = int(packed & 0xFFFFu);
    if (bits >= 32768)
        bits -= 65536;

    return max(float(bits) / 32767.0, -1.0);
}
