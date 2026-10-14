// Values packed into fewer bits: what the G-buffer and other targets store.

#ifndef MAGIC_PACKING_HLSLI
#define MAGIC_PACKING_HLSLI

// A unit vector as two values in -1..1 on the octahedron (Cigolle et al. 2014, "A Survey of Efficient
// Representations for Independent Unit Vectors"): precision spread evenly over the sphere, with no pole to
// break at. In 10 + 10 bits it stays within about 0.2 degrees.
float2 OctEncode(float3 direction)
{
    float3 projected = direction / (abs(direction.x) + abs(direction.y) + abs(direction.z));
    float2 signs = select(projected.xy >= 0.0, float2(1.0, 1.0), float2(-1.0, -1.0));
    float2 folded = (1.0 - abs(projected.yx)) * signs;
    return projected.z >= 0.0 ? projected.xy : folded;
}

float3 OctDecode(float2 encoded)
{
    float3 direction = float3(encoded, 1.0 - abs(encoded.x) - abs(encoded.y));
    float fold = saturate(-direction.z);
    direction.xy += select(direction.xy >= 0.0, float2(-fold, -fold), float2(fold, fold));
    return normalize(direction);
}

#endif
