// Smooths and, at half resolution, upsamples the raw ambient occlusion into the texel the lighting reads: for
// every pixel of the view, the 4 x 4 raw texels around it (one period of the dither pattern) weighed by how
// close in depth each is to the pixel, so the pattern goes but the darkening does not bleed across an edge
// onto a surface at another depth. The bent normals average the same way. One thread per pixel of the view.

#include "Include/Shade.hlsli"
#include "Lighting/AoCommon.hlsli"

#define GROUP_SIZE 8

// A tap this many times its depth away from the pixel counts for nothing much.
static const float DepthTolerance = 0.04;

Texture2D<float4> AoRaw : READ(0);
SamplerState AoRawSampler : SAMPLER(0);
Texture2D<float> Depth : READ(1);
SamplerState DepthSampler : SAMPLER(1);

[[vk::image_format("rgba8")]]
RWTexture2D<float4> Ao : WRITE(0);

[numthreads(GROUP_SIZE, GROUP_SIZE, 1)]
void main(uint3 threadId : SV_DispatchThreadID)
{
    uint2 viewSize = (uint2)ViewSize;
    uint2 viewPixel = threadId.xy;
    if (any(viewPixel >= viewSize))
        return;

    uint targetWidth, targetHeight;
    Depth.GetDimensions(targetWidth, targetHeight);
    uint rawWidth, rawHeight;
    AoRaw.GetDimensions(rawWidth, rawHeight);
    float2 rawSize = float2(rawWidth, rawHeight);

    uint2 pixel = (uint2)ViewOrigin + viewPixel;
    float depth = Depth.SampleLevel(DepthSampler, (float2(pixel) + 0.5) / float2(targetWidth, targetHeight), 0.0);
    float4 result = EncodeAo(float3(0.0, 0.0, 1.0), 1.0);
    [branch] if (depth > 0.0 && (AoFlags & AoEnabledFlag) != 0u)
    {
        float viewDepth = ViewDepth(depth);

        // The raw texel under the pixel, and the 4 x 4 around it, kept inside the view's own rectangle of the raw target.
        float2 aoOrigin = floor(ViewOrigin * AoScale);
        float2 aoSize = ceil(ViewSize * AoScale);
        float2 center = aoOrigin + (float2(viewPixel) + 0.5) * AoScale - 0.5;
        float2 first = floor(center) - 1.0;
        float2 last = aoOrigin + aoSize - 1.0;

        float weightSum = 0.0;
        float visibility = 0.0;
        float3 bent = float3(0.0, 0.0, 0.0);
        float nearestVisibility = 1.0;
        float3 nearestBent = float3(0.0, 0.0, 1.0);
        float nearestDistance = 1e9;
        [unroll] for (int y = 0; y < 4; y++)
        {
            [unroll] for (int x = 0; x < 4; x++)
            {
                float2 at = clamp(first + float2(x, y), aoOrigin, last);
                float4 raw = AoRaw.SampleLevel(AoRawSampler, (at + 0.5) / rawSize, 0.0);
                float3 tapBent = OctahedralDecode(raw.zw);
                float depthGap = abs(raw.y - viewDepth) / max(viewDepth * DepthTolerance, 1e-4);
                float weight = exp(-depthGap * depthGap);
                weightSum += weight;
                visibility += weight * raw.x;
                bent += weight * tapBent;

                float distance = dot(at - center, at - center);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearestVisibility = raw.x;
                    nearestBent = tapBent;
                }
            }
        }

        // No tap at the pixel's depth (a sliver of a surface): the nearest one as it is.
        bool anyWeight = weightSum > 1e-3;
        visibility = anyWeight ? visibility / weightSum : nearestVisibility;
        float3 bentView = anyWeight ? NormalizeOrZero(bent) : nearestBent;
        result = EncodeAo(ViewToWorld(bentView), visibility);
    }

    Ao[pixel] = result;
}
