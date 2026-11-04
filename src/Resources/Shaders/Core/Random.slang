// Numbers that look random and repeat exactly: hashes of a pixel, a frame and a sample index, so a stochastic pass is
// the same from one run to the next and from one GPU to another.
module "Core/Random";

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
