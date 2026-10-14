// The debug UI over the window: 2D triangles in window pixels, +Y down from the top left corner, each vertex with its
// texture coordinate and an RGBA8 colour, straight from the UI gem's vertex buffer. Drawn onto the window's image
// after the scene, so not a surface and not lit. OverlayVaryings must match Overlay.frag.hlsl's.

#include "Include/Bindings.hlsli"

cbuffer Overlay : UNIFORM(0)
{
    float2 InvTargetSize; // 1 / the target's size in pixels
    float2 OverlayPad;
};

struct OverlayVertex
{
    float2 position : TEXCOORD0;
    float2 uv : TEXCOORD1;
    float4 color : TEXCOORD2; // RGBA8, read as 0 to 1
};

struct OverlayVaryings
{
    float2 uv : TEXCOORD0;
    float4 color : TEXCOORD1;
    float4 position : SV_Position;
};

OverlayVaryings main(OverlayVertex input)
{
    float2 ndc = input.position * InvTargetSize * 2.0 - 1.0;

    OverlayVaryings output;
    output.position = float4(ndc.x, -ndc.y, 0.0, 1.0);
    output.uv = input.uv;
    output.color = input.color;
    return output;
}
