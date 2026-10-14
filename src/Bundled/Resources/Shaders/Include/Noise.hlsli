// Noise for stochastic effects (ambient occlusion, reflections, shadow filtering, dithering), meant to be resolved
// over frames by temporal anti-aliasing: cheap, deterministic, and with no texture to bind.

#ifndef MAGIC_NOISE_HLSLI
#define MAGIC_NOISE_HLSLI

// Interleaved gradient noise (Jimenez 2014, "Next Generation Post Processing in Call of Duty: Advanced Warfare"):
// 0..1 per pixel, with little low-frequency energy, so a few samples per pixel already look smooth. The frame
// moves it on each frame, so a temporal filter gathers different samples over time.
float InterleavedGradientNoise(float2 pixel, uint frame)
{
    float2 moved = pixel + 5.588238 * float(frame % 64u);
    return frac(52.9829189 * frac(dot(moved, float2(0.06711056, 0.00583715))));
}

// The index-th point of the R2 sequence (Roberts 2018, "The Unreasonable Effectiveness of Quasirandom
// Sequences"): 2D points in 0..1 that cover the square evenly at any count, for sample offsets and jitter.
float2 R2(uint index)
{
    return frac(0.5 + float(index) * float2(0.7548776662, 0.5698402910));
}

// The index-th of count points of the Hammersley set: the even, stratified 2D samples importance sampling
// integrates with.
float2 Hammersley(uint index, uint count)
{
    return float2(float(index) / float(count), float(reversebits(index)) * 2.3283064365386963e-10);
}

// 32 well-mixed bits from 32 (PCG; Jarzynski & Olano 2020, "Hash Functions for GPU Rendering").
uint PcgHash(uint value)
{
    uint state = value * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return (word >> 22u) ^ word;
}

// A hash as a float in 0..1, from its top 24 bits.
float HashToUnit(uint hash)
{
    return float(hash >> 8u) * (1.0 / 16777216.0);
}

// Noise of a triangular distribution in -1..1 for dithering, one step of the target's quantisation wide on each
// side: it hides banding without its strength varying with the signal, as uniform noise's does (Gjoel 2016,
// "Banding in Games").
float TriangularDither(float2 pixel, uint frame)
{
    float first = InterleavedGradientNoise(pixel, frame);
    float second = HashToUnit(PcgHash(uint(pixel.x) + uint(pixel.y) * 65536u + frame * 7919u));
    return first + second - 1.0;
}

#endif
