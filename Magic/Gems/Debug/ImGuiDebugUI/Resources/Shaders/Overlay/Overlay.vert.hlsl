// Overlays: 2D triangles in window pixels (+Y down, (0, 0) top left) with an RGBA8 vertex colour, straight
// from the UI gem's vertex buffer. Not a material shader; drawn over the swapchain image, after the scene.
// VsOut must match PsIn in Overlay.frag.hlsl.

#include "Include/Bindings.hlsli"

cbuffer Overlay : UNIFORM(0)
{
    float2 InvWindowSize; // 1 / window size in pixels
    float2 OverlayPad;
};

struct VsIn
{
    float2 position : TEXCOORD0;
    float2 uv : TEXCOORD1;
    float4 color : TEXCOORD2; // R8G8B8A8 unorm
};

struct VsOut
{
    float2 uv : TEXCOORD0;
    float4 color : TEXCOORD1;
    float4 position : SV_Position;
};

VsOut main(VsIn input)
{
    float2 ndc = input.position * InvWindowSize * 2.0 - 1.0;

    VsOut output;
    output.position = float4(ndc.x, -ndc.y, 0.0, 1.0);
    output.uv = input.uv;
    output.color = input.color;
    return output;
}
