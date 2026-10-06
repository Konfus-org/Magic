// Readies the transparency stage for a view, one thread per pixel: the lit scene as it is before any transparent
// surface, copied into SceneColor (what a refracting surface shows behind it, Include/Surface.hlsli, and what
// Transparency/Composite.comp.hlsl puts the layers over), and the transparency targets emptied for the layers
// Templates/Forward.frag.hlsl adds. A render pass cannot empty them: its clear would empty the opaque depth too.
// Run again between the layers of refracting glass, so each layer shows what the ones before it made; there the
// nearest depth is kept (CLEAR_FRONT 0), the first run empties it too.

#include "Include/Frame.hlsli"

#define GROUP_SIZE 8

Texture2D<float4> Hdr : READ(0);
SamplerState HdrSampler : SAMPLER(0);

[[vk::image_format("rgba16f")]]
RWTexture2D<float4> SceneColor : WRITE(0);
[[vk::image_format("rgba16f")]]
RWTexture2D<float4> TransparentAccum : WRITE(1);
[[vk::image_format("r16f")]]
RWTexture2D<float> TransparentCoverage : WRITE(2);
#ifndef CLEAR_FRONT
#define CLEAR_FRONT 0
#endif
#if CLEAR_FRONT
[[vk::image_format("r32f")]]
RWTexture2D<float> TransparentFront : WRITE(3);
#endif

[numthreads(GROUP_SIZE, GROUP_SIZE, 1)]
void main(uint3 threadId : SV_DispatchThreadID)
{
    uint2 viewPixel = threadId.xy;
    if (any(viewPixel >= (uint2)ViewSize))
        return;

    uint width, height;
    Hdr.GetDimensions(width, height);
    uint2 pixel = (uint2)ViewOrigin + viewPixel;
    SceneColor[pixel] = Hdr.SampleLevel(HdrSampler, (float2(pixel) + 0.5) / float2(width, height), 0.0);
    TransparentAccum[pixel] = float4(0.0, 0.0, 0.0, 0.0);
    TransparentCoverage[pixel] = 0.0;
#if CLEAR_FRONT
    TransparentFront[pixel] = 0.0;
#endif
}
