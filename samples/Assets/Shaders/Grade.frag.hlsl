// The samples' own grade, after the tonemap: how saturated the view is, how much contrast it has about middle grey, a
// tint over it and a vignette darkening its corners, from the copy GradeCopy made, back into the view the output shows.
// A look is a pipeline with this pass and its params; at its params' defaults it changes nothing.

#include "Include/Fullscreen.hlsli"

struct PassParams
{
    float saturation = 1.0; // 0 grey, 1 as it is
    float contrast = 1.0;   // about middle grey, 1 as it is
    float3 tint = float3(1.0, 1.0, 1.0);
    float vignette = 0.0;   // how dark the corners go, 0 none
};

float4 main(PassVaryings input) : SV_Target0
{
    PassParams params = LoadPassParams();
    float3 color = Toned.SampleLevel(TonedSampler, input.uv, 0.0).rgb;
    float grey = dot(color, float3(0.2126, 0.7152, 0.0722));
    color = lerp(float3(grey, grey, grey), color, params.saturation);
    color = (color - 0.18) * params.contrast + 0.18;
    color *= params.tint;
    float2 fromMiddle = input.uv - 0.5;
    color *= 1.0 - params.vignette * smoothstep(0.2, 0.8, dot(fromMiddle, fromMiddle) * 2.0);
    return float4(saturate(color), 1.0);
}
