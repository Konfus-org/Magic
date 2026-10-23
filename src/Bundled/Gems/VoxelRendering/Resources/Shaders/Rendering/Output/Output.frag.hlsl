// A view shown in its target: its finished image scaled into the view's rectangle, with the words of what failed this
// frame over it (Include/FailureBanner.hlsli), encoded as the target's image expects: as it is for a render texture,
// which is linear, and for a window whose sRGB-typed image the GPU encodes; sRGB for an SDR window's UNORM image,
// dithered by a step of 8 bits so gradients do not band; PQ on Rec. 2020 for an HDR10 window's, dithered by its 10 bits;
// scRGB for an HDR linear window's, where 1.0 is 80 nits. On an HDR display the finished image, tone-mapped to 0..1,
// shows as it would on an SDR one, its 1.0 at paper white.

#include "Include/Color.hlsli"
#include "Include/FailureBanner.hlsli"
#include "Include/Fullscreen.hlsli"
#include "Include/Heap.hlsli"
#include "Include/Noise.hlsli"

struct OutputConstants
{
    uint image;              // the finished image
    uint sampler;            // what it is scaled through
    uint text;               // the failure banner's glyphs
    uint failures;           // the frame's raised codes
    float width;             // the view's size as it is shown
    float height;
    float time;
    uint encoding;           // 0 linear as it is, 1 sRGB, 2 PQ (HDR10), 3 scRGB
    uint frameIndex;         // which dithers it
};

CONSTANTS(OutputConstants, c);

// Nits the finished image's 1.0 shows as on an HDR display: an SDR display's brightest white, about.
static const float PaperWhiteNits = 200.0;

float3 EncodeForTarget(float3 color, float2 pixel)
{
    [branch] if (c.encoding == 1u)
    {
        return LinearToSrgb(saturate(color)) + TriangularDither(pixel, c.frameIndex) / 255.0;
    }
    else if (c.encoding == 2u)
    {
        float3 pq = LinearToPq(Rec709ToRec2020(saturate(color)) * PaperWhiteNits);
        return pq + TriangularDither(pixel, c.frameIndex) / 1023.0;
    }
    else if (c.encoding == 3u)
    {
        return saturate(color) * (PaperWhiteNits / 80.0);
    }

    return color;
}

float4 main(PassVaryings input) : SV_Target0
{
    float2 size = float2(c.width, c.height);
    float3 color = TEXTURE(c.image).SampleLevel(SAMPLER_STATE(c.sampler), input.uv, 0.0).rgb;
    color = FailureBanner(color, uint2(input.uv * size), uint2(size), BUFFER(c.text), BUFFER(c.failures), c.time);
    return float4(EncodeForTarget(color, input.position.xy), 1.0);
}
