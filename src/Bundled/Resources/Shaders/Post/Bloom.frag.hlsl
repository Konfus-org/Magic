// Bloom: the light a lens scatters around what is bright, so a highlight brighter than the display can show glows by
// how bright it is (Jimenez 2014, "Next Generation Post Processing in Call of Duty: Advanced Warfare"). Each level of
// BloomChain, the view halved again and again (Post/BloomDownsample.frag.hlsl), is read through a tent of nine taps a
// texel of that level apart, and the levels are averaged: a blur of each octave counted alike falls off about as one
// over the square of the distance, as a lens's glare does. There is no threshold: every pixel gives the glow its share,
// and only what is bright shows it. The glow is handed to tonemapping premultiplied, times the strength with the
// strength as its alpha, to be laid over the view, which keeps the share it gave away less.

#include "Include/Fullscreen.hlsli"

struct PassParams
{
    float strength = 0.04;   // the share of each pixel's light the glow takes, 0 to 1
};

// A level of BloomChain about uv through a tent of nine taps a texel of the level apart: 1 2 1, 2 4 2, 1 2 1 over 16.
float3 Tent(float2 uv, float2 texel, float level)
{
    float3 middle = BloomChain.SampleLevel(BloomChainSampler, uv, level).rgb;
    float3 sides = BloomChain.SampleLevel(BloomChainSampler, uv + float2(-texel.x, 0.0), level).rgb
        + BloomChain.SampleLevel(BloomChainSampler, uv + float2(texel.x, 0.0), level).rgb
        + BloomChain.SampleLevel(BloomChainSampler, uv + float2(0.0, -texel.y), level).rgb
        + BloomChain.SampleLevel(BloomChainSampler, uv + float2(0.0, texel.y), level).rgb;
    float3 corners = BloomChain.SampleLevel(BloomChainSampler, uv + float2(-texel.x, -texel.y), level).rgb
        + BloomChain.SampleLevel(BloomChainSampler, uv + float2(texel.x, -texel.y), level).rgb
        + BloomChain.SampleLevel(BloomChainSampler, uv + float2(-texel.x, texel.y), level).rgb
        + BloomChain.SampleLevel(BloomChainSampler, uv + float2(texel.x, texel.y), level).rgb;
    return (middle * 4.0 + sides * 2.0 + corners) / 16.0;
}

float4 main(PassVaryings input) : SV_Target0
{
    PassParams params = LoadPassParams();
    float width;
    float height;
    float levels;
    BloomChain.GetDimensions(0u, width, height, levels);
    float3 glow = float3(0.0, 0.0, 0.0);
    [loop] for (float level = 0.0; level < levels; level += 1.0)
    {
        glow += Tent(input.uv, exp2(level) / float2(width, height), level);
    }

    float strength = saturate(params.strength);
    return float4(glow / max(levels, 1.0) * strength, strength);
}
