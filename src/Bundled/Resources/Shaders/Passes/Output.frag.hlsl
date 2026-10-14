// A view shown in its target: its finished image scaled into the view's rectangle, and for a window, whose image is
// UNORM, encoded to sRGB and dithered by a step of 8 bits so gradients do not band; a render texture is linear, and
// takes it as it is.

#include "Include/Color.hlsli"
#include "Include/Frame.hlsli"
#include "Include/Fullscreen.hlsli"
#include "Include/Noise.hlsli"

Texture2D<float4> Source : READ(0);
SamplerState SourceSampler : SAMPLER(0);

float4 main(PassVaryings input) : SV_Target0
{
    float3 color = Source.SampleLevel(SourceSampler, input.uv, 0.0).rgb;
    [branch] if (TargetIsWindow != 0u)
    {
        color = LinearToSrgb(saturate(color)) + TriangularDither(input.position.xy, FrameIndex) / 255.0;
    }

    return float4(color, 1.0);
}
