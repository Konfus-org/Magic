// Puts the transparent layers over the scene, one thread per pixel of the view: their weighted average colour
// (Templates/Forward.frag.hlsl summed colour times weight, and weight) over the scene as it was before them
// (SceneColor), by how much of it they cover. A pixel no layer covers keeps the scene, which Hdr already holds.

#include "Include/Frame.hlsli"

#define GROUP_SIZE 8

Texture2D<float4> SceneColor : READ(0);
SamplerState SceneColorSampler : SAMPLER(0);
Texture2D<float4> TransparentAccum : READ(1);
SamplerState TransparentAccumSampler : SAMPLER(1);
Texture2D<float> TransparentCoverage : READ(2);
SamplerState TransparentCoverageSampler : SAMPLER(2);

[[vk::image_format("rgba16f")]]
RWTexture2D<float4> Hdr : WRITE(0);

[numthreads(GROUP_SIZE, GROUP_SIZE, 1)]
void main(uint3 threadId : SV_DispatchThreadID)
{
    uint2 viewPixel = threadId.xy;
    if (any(viewPixel >= (uint2)ViewSize))
        return;

    uint width, height;
    SceneColor.GetDimensions(width, height);
    uint2 pixel = (uint2)ViewOrigin + viewPixel;
    float2 uv = (float2(pixel) + 0.5) / float2(width, height);
    float coverage = saturate(TransparentCoverage.SampleLevel(TransparentCoverageSampler, uv, 0.0));
    [branch] if (coverage <= 0.0)
        return;

    float4 accum = TransparentAccum.SampleLevel(TransparentAccumSampler, uv, 0.0);
    float3 layers = accum.rgb / max(accum.a, 1e-5);
    float4 scene = SceneColor.SampleLevel(SceneColorSampler, uv, 0.0);
    Hdr[pixel] = float4(lerp(scene.rgb, layers, coverage), scene.a);
}
