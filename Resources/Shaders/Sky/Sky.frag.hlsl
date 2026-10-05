// The sky: what shows where nothing was drawn. A fullscreen pass over the render target after the lighting,
// writing Hdr only where the depth is empty (0, reverse-Z) and leaving every lit pixel as it is: the sky's colour
// when a Sky entity set one (the frame's Ambient, FrameFlagHasSky), else the engine's clear colour (twin:
// RenderCommands.ClearColor). The gbuffer is cleared black, so without this pass the background is black: the
// background is the pipeline's to decide.

#include "Include/Pass.hlsli"

static const float3 DefaultSkyColor = float3(0.02, 0.03, 0.08);

Texture2D<float> Depth : READ(0);
SamplerState DepthSampler : SAMPLER(0);

float4 main(PassVaryings input) : SV_Target0
{
    float depth = Depth.SampleLevel(DepthSampler, input.uv, 0.0);
    if (depth > 0.0)
        discard;

    float3 sky = FrameHas(FrameFlagHasSky) ? Ambient : DefaultSkyColor;
    return float4(sky, 1.0);
}
