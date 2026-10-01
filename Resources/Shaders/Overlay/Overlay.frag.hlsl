// Overlays: the command's texture (for ImGui, its atlas: glyphs, a white pixel for solid shapes) times the
// vertex colour, alpha blended. PsIn must match VsOut in Overlay.vert.hlsl.

#include "Include/Bindings.hlsli"

Texture2D<float4> Atlas : READ(0);
SamplerState AtlasSampler : SAMPLER(0);

struct PsIn
{
    float2 uv : TEXCOORD0;
    float4 color : TEXCOORD1;
};

float4 main(PsIn input) : SV_Target0
{
    return Atlas.Sample(AtlasSampler, input.uv) * input.color;
}
