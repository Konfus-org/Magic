// How a view's image is encoded as it is shown into its target (TargetEncoding, from the target's format): as it is
// for a render texture, which is linear, and for a window whose sRGB-typed image the GPU encodes; sRGB for an SDR
// window's UNORM image, dithered by a step of 8 bits so gradients do not band; PQ on Rec. 2020 for an HDR10 window's,
// dithered by its 10 bits; scRGB for an HDR linear window's, where 1.0 is 80 nits. On an HDR display the finished
// image, tone-mapped to 0..1, shows as it would on an SDR one, its 1.0 at paper white.

#ifndef MAGIC_TARGET_ENCODE_HLSLI
#define MAGIC_TARGET_ENCODE_HLSLI

#include "Include/Color.hlsli"
#include "Include/Frame.hlsli"
#include "Include/Noise.hlsli"

// Nits the finished image's 1.0 shows as on an HDR display: an SDR display's brightest white, about.
static const float PaperWhiteNits = 200.0;

// color at the target's pixel, encoded for the target.
float3 EncodeForTarget(float3 color, float2 pixel)
{
    [branch] if (TargetEncoding == 1u)
    {
        return LinearToSrgb(saturate(color)) + TriangularDither(pixel, FrameIndex) / 255.0;
    }
    else if (TargetEncoding == 2u)
    {
        float3 pq = LinearToPq(Rec709ToRec2020(saturate(color)) * PaperWhiteNits);
        return pq + TriangularDither(pixel, FrameIndex) / 1023.0;
    }
    else if (TargetEncoding == 3u)
    {
        return saturate(color) * (PaperWhiteNits / 80.0);
    }

    return color;
}

#endif
