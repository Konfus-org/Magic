// The debug UI over the window: 2D triangles in window pixels, +Y down from the top left corner, each vertex with its
// texture coordinate and an RGBA8 colour, read from the UI's vertex buffer by index, 20 bytes each. Drawn onto the
// window's image after the scene, so not a surface and not lit. OverlayVaryings must match Overlay.frag.hlsl's, and
// OverlayConstants the overlay script's.

#include "Include/Heap.hlsli"

struct OverlayConstants
{
    float2 invTargetSize;    // 1 / the target's size in pixels
    uint vertices;           // the UI's vertices: position, uv, RGBA8 colour
    uint firstVertex;        // the draw's first vertex in them, which its indices count from
    uint texture;            // the draw's texture
    uint sampler;
    uint encoding;           // 0 linear, 1 sRGB as ImGui's colours are, 2 PQ (HDR10), 3 scRGB
    uint padding;
};

CONSTANTS(OverlayConstants, c);

struct OverlayVaryings
{
    float2 uv : TEXCOORD0;
    float4 color : TEXCOORD1;
    float4 position : SV_Position;
};

OverlayVaryings main(uint index : SV_VertexID)
{
    uint at = (c.firstVertex + index) * 20u;
    float2 position = BUFFER(c.vertices).Load<float2>(at);
    uint color = BUFFER(c.vertices).Load(at + 16u);
    float2 ndc = position * c.invTargetSize * 2.0 - 1.0;

    OverlayVaryings output;
    output.position = float4(ndc.x, -ndc.y, 0.0, 1.0);
    output.uv = BUFFER(c.vertices).Load<float2>(at + 8u);
    output.color = float4(color & 0xFFu, (color >> 8) & 0xFFu, (color >> 16) & 0xFFu, color >> 24) / 255.0;
    return output;
}
