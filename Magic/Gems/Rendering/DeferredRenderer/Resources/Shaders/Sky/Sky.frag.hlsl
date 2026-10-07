// The sky: what shows where nothing was drawn. A fullscreen pass over the render target after the lighting,
// writing Hdr only where the depth is empty (0, reverse-Z) and leaving every lit pixel as it is: the sky's colour
// when a Sky entity set one (the frame's Ambient, FrameFlagHasSky), else the engine's background (twin:
// Fallbacks.Background). The gbuffer is cleared black, so without this pass the background is black: the
// background is the pipeline's to decide.

#include "Include/Pass.hlsli"


Texture2D<float> Depth : READ(0);
SamplerState DepthSampler : SAMPLER(0);

float4 main(PassVaryings input) : SV_Target0
{
    float depth = Depth.SampleLevel(DepthSampler, input.uv, 0.0);
    if (depth > 0.0)
        discard;

    float3 sky = FrameHas(FrameFlagHasSky) ? Ambient : float3(BackgroundR, BackgroundG, BackgroundB);
    return float4(sky, 1.0);
}
