// The fragment half of a glow (Glow.vert.hlsl): the disk inside the square, written into the gbuffer as
// emission only. Its base colour is black, so the lighting adds nothing to it.
//
// PsIn here and VsOut in Glow.vert.hlsl are one struct written twice: keep them the same, member for member.

#include "Include/GBuffer.hlsli"

struct PsIn
{
    float2 corner : TEXCOORD0; // -1..1 across the square
    float3 color : TEXCOORD1;
    float4 position : SV_Position;
};

GBufferOutput main(PsIn input)
{
    clip(1.0 - dot(input.corner, input.corner));

    GBufferSurface surface;
    surface.emissive = input.color;
    surface.baseColor = float3(0.0, 0.0, 0.0);
    surface.normal = ViewToWorld(float3(0.0, 0.0, -1.0)); // facing the camera; nothing lights it
    surface.roughness = 1.0;
    surface.metallic = 0.0;
    surface.occlusion = 1.0;
    return EncodeGBuffer(surface);
}
