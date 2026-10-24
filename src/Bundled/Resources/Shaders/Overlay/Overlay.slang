// The debug UI over the window: 2D triangles in window pixels, +Y down from the top left corner, each vertex with its
// texture coordinate and an RGBA8 colour, read from the UI's vertices by index (DrawOverlay), then the draw's texture
// (for ImGui, its atlas of characters, with a white texel for solid shapes) times the vertex colour, blended over the
// image by its alpha (ShadeOverlay). Drawn onto the window's image after the scene, so not a surface and not lit.
// OverlayConstants is the overlay script's, member for member.
//
// ImGui's colours are encoded for an SDR window's UNORM image, so that image takes them as they are, and any other
// takes them decoded to linear and encoded as its format expects (Core/Color's EncodeForTarget, as the renderer's
// output does for the scene). The GPU blends in the image's space, so translucent panes blend in sRGB code over an SDR
// image, as ImGui means them to, in PQ code over an HDR10 one, and in linear over the others, a touch lighter. A new
// overlay draws with these entry points and its own vertices, or with its own entry points encoding the same way.

module "Overlay/Overlay";

import Core.Color;
import Core.Heap;

// One of the UI's vertices, as ImGui writes it.
struct OverlayVertex
{
    float2 position;
    float2 uv;
    uint color;              // RGBA8
}

struct OverlayConstants
{
    float2 invTargetSize;    // 1 / the target's size in pixels
    ArrayRef<OverlayVertex> vertices;
    uint firstVertex;        // the draw's first vertex in them, which its indices count from
    TextureRef texture;      // the draw's texture
    SamplerRef sampler;
    uint encoding;           // the target's TargetEncoding
}

struct OverlayVaryings
{
    float2 uv : TEXCOORD0;
    float4 color : TEXCOORD1;
    float4 position : SV_Position;
}

[shader("vertex")]
OverlayVaryings DrawOverlay(uint index : SV_VulkanVertexID, uniform OverlayConstants c)
{
    OverlayVertex vertex = c.vertices[c.firstVertex + index];
    float2 ndc = vertex.position * c.invTargetSize * 2.0 - 1.0;
    uint color = vertex.color;

    OverlayVaryings output;
    output.position = float4(ndc.x, -ndc.y, 0.0, 1.0);
    output.uv = vertex.uv;
    output.color = float4(color & 0xFFu, (color >> 8) & 0xFFu, (color >> 16) & 0xFFu, color >> 24) / 255.0;
    return output;
}

[shader("fragment")]
float4 ShadeOverlay(OverlayVaryings input, uniform OverlayConstants c) : SV_Target0
{
    float4 color = c.texture.Sample(c.sampler, input.uv) * input.color;
    TargetEncoding encoding = (TargetEncoding)c.encoding;
    [branch] if (encoding == TargetEncoding.Srgb)
    {
        return color;
    }

    return float4(EncodeForTarget(DecodeSrgb(color.rgb), encoding), color.a);
}
