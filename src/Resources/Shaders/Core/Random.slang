// Numbers that look random and repeat exactly: hashes of a pixel, a frame and a sample index, so a stochastic pass is
// the same from one run to the next and from one GPU to another.
module "Core/Random";

import Core.Heap;

// A well-mixed word from one (O'Neill's PCG, the 32-bit output permutation).
public uint GetHash(uint seed)
{
    uint state = seed * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return (word >> 22u) ^ word;
}

// Three well-mixed words from three, each depending on all (Jarzynski and Olano, "Hash Functions for GPU Rendering",
// 2020, pcg3d).
public uint3 GetHash(uint3 seed)
{
    uint3 v = seed * 1664525u + 1013904223u;
    v.x += v.y * v.z;
    v.y += v.z * v.x;
    v.z += v.x * v.y;
    v ^= v >> 16u;
    v.x += v.y * v.z;
    v.y += v.z * v.x;
    v.z += v.x * v.y;
    return v;
}

// A word's top bits as a number in [0, 1).
public float ToUnitFloat(uint hash) { return float(hash >> 8u) * (1.0 / 16777216.0); }

// Three numbers in [0, 1) for a pixel, a frame and a sample index.
public float3 GetRandom(uint2 pixel, uint frame, uint sample)
{
    uint3 hash = GetHash(uint3(pixel, frame * 65536u + sample));
    return float3(ToUnitFloat(hash.x), ToUnitFloat(hash.y), ToUnitFloat(hash.z));
}

// The side of the blue-noise tile in pixels, the twin of C#'s BlueNoise.Size.
public static const uint BlueNoiseSize = 64u;

// Two numbers in [0, 1) for a pixel, a frame and a sample index, from a buffer of blue noise, a float a pixel of its
// tile row by row: the tile read at the pixel and half a tile across, shifted by the sample, so neighbours differ as
// much as they can, and each moved by the frame along the R2 sequence, so a pixel's frames in a row spread evenly.
public float2 GetBlueNoise(uint noise, uint2 pixel, uint frame, uint sample)
{
    ArrayRef<float> values = { noise };
    uint2 first = (pixel + sample * uint2(17u, 41u)) % BlueNoiseSize;
    uint2 second = (first + BlueNoiseSize / 2u) % BlueNoiseSize;
    float2 tile = float2(values[first.y * BlueNoiseSize + first.x], values[second.y * BlueNoiseSize + second.x]);
    return frac(tile + float(frame % 4096u) * float2(0.7548776662, 0.5698402910));
}

// A number in [0, 1) for a pixel that changes smoothly in no direction, so a pattern rotated by it has no visible
// structure, moved by 5.588238 pixels each frame so a pixel's frames differ and temporal anti-aliasing averages them
// (Jimenez, "Next Generation Post Processing in Call of Duty: Advanced Warfare", SIGGRAPH 2014).
public float GetInterleavedGradientNoise(uint2 pixel, uint frame)
{
    float2 position = float2(pixel) + 5.588238 * float(frame % 64u);
    return frac(52.9829189 * frac(dot(position, float2(0.06711056, 0.00583715))));
}
