// A view shown in its target: its finished image scaled into the view's rectangle, with the words of what failed this
// frame over it (Include/FailureBanner.hlsli), encoded as the target's image expects (Include/TargetEncode.hlsli).

#include "Include/FailureBanner.hlsli"
#include "Include/Fullscreen.hlsli"
#include "Include/TargetEncode.hlsli"

Texture2D<float4> Source : READ(0);
SamplerState SourceSampler : SAMPLER(0);
ByteAddressBuffer FailureText : READ(1);
ByteAddressBuffer Failures : READ(2);

float4 main(PassVaryings input) : SV_Target0
{
    float3 color = Source.SampleLevel(SourceSampler, input.uv, 0.0).rgb;
    color = FailureBanner(color, uint2(input.uv * OutputSize), uint2(OutputSize), FailureText, Failures, Time);
    return float4(EncodeForTarget(color, input.position.xy), 1.0);
}
