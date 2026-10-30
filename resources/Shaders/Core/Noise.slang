// Noise for stochastic effects (dithering, sampling), meant to be resolved over frames by temporal anti-aliasing, and
// smooth noise for what is drawn as a pattern (clouds): cheap, deterministic, and with no texture to bind.

module "Core/Noise";

// Interleaved gradient noise (Jimenez 2014, "Next Generation Post Processing in Call of Duty: Advanced Warfare"):
// 0..1 per pixel, with little low-frequency energy, so a few samples per pixel already look smooth. The frame
// moves it on each frame, so a temporal filter gathers different samples over time.
public float GetInterleavedGradientNoise(float2 pixel, uint frame)
{
    float2 moved = pixel + 5.588238 * float(frame % 64u);
    return frac(52.9829189 * frac(dot(moved, float2(0.06711056, 0.00583715))));
}

// The index-th of count points of the Hammersley set: the even, stratified 2D samples importance sampling
// integrates with.
public float2 GetHammersley(uint index, uint count)
{
    return float2(float(index) / float(count), float(reversebits(index)) * 2.3283064365386963e-10);
}

// 32 well-mixed bits from 32 (PCG; Jarzynski & Olano 2020, "Hash Functions for GPU Rendering").
public uint Hash(uint value)
{
    uint state = value * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return (word >> 22u) ^ word;
}

// A hash as a float in 0..1, from its top 24 bits.
public float ToUnitInterval(uint hash)
{
    return float(hash >> 8u) * (1.0 / 16777216.0);
}

// Noise of a triangular distribution in -1..1 for dithering, one step of the target's quantisation wide on each
// side: it hides banding without its strength varying with the signal, as uniform noise's does (Gjoel 2016,
// "Banding in Games").
public float GetTriangularDither(float2 pixel, uint frame)
{
    float first = GetInterleavedGradientNoise(pixel, frame);
    float second = ToUnitInterval(Hash(uint(pixel.x) + uint(pixel.y) * 65536u + frame * 7919u));
    return first + second - 1.0;
}

// The dot of one of the twelve directions to a cube's edges, picked by a hash, with an offset from a lattice point:
// the gradients of improved noise (Perlin 2002, "Improving Noise"), which need no table of directions.
float GetGradientDot(uint hash, float3 offset)
{
    uint h = hash & 15u;
    float u = h < 8u ? offset.x : offset.y;
    float v = h < 4u ? offset.y : (h == 12u || h == 14u ? offset.x : offset.z);
    return ((h & 1u) == 0u ? u : -u) + ((h & 2u) == 0u ? v : -v);
}

// A hash of a lattice point, the same for the same point however it is reached.
public uint HashLattice(int3 cell)
{
    return Hash(asuint(cell.x) + Hash(asuint(cell.y) + Hash(asuint(cell.z))));
}

// The gradient at a corner of position's lattice cell, offset from it.
float GetCornerDot(int3 corner, float3 inside, int3 step)
{
    return GetGradientDot(HashLattice(corner + step), inside - float3(step));
}

// Improved gradient noise (Perlin 2002): smooth, about -1 to 1, 0 at every lattice point, its gradients at the corners
// of the point's cell blended by the quintic fade, so its slope is continuous too. Lattice hashes stand in for the
// paper's permutation table.
public float GetGradientNoise(float3 position)
{
    float3 cell = floor(position);
    float3 inside = position - cell;
    float3 fade = inside * inside * inside * (inside * (inside * 6.0 - 15.0) + 10.0);
    int3 corner = int3(cell);
    float x00 = lerp(GetCornerDot(corner, inside, int3(0, 0, 0)), GetCornerDot(corner, inside, int3(1, 0, 0)), fade.x);
    float x10 = lerp(GetCornerDot(corner, inside, int3(0, 1, 0)), GetCornerDot(corner, inside, int3(1, 1, 0)), fade.x);
    float x01 = lerp(GetCornerDot(corner, inside, int3(0, 0, 1)), GetCornerDot(corner, inside, int3(1, 0, 1)), fade.x);
    float x11 = lerp(GetCornerDot(corner, inside, int3(0, 1, 1)), GetCornerDot(corner, inside, int3(1, 1, 1)), fade.x);
    return lerp(lerp(x00, x10, fade.y), lerp(x01, x11, fade.y), fade.z);
}
