// The debug UI over the window: the draw's texture (for ImGui, its atlas of characters, with a white texel for solid
// shapes) times the vertex colour, blended over the image by its alpha. ImGui's colours are already encoded for the
// window's UNORM image, so they are written as they are. OverlayVaryings must match Overlay.vert.hlsl's.

#include "Include/Bindings.hlsli"

Texture2D<float4> Atlas : READ(0);
SamplerState AtlasSampler : SAMPLER(0);

struct OverlayVaryings
{
    float2 uv : TEXCOORD0;
    float4 color : TEXCOORD1;
    float4 position : SV_Position;
};

float4 main(OverlayVaryings input) : SV_Target0
{
    return Atlas.Sample(AtlasSampler, input.uv) * input.color;
}
