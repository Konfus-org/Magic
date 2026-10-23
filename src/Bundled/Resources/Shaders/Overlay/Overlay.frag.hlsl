// The debug UI over the window: the draw's texture (for ImGui, its atlas of characters, with a white texel for solid
// shapes) times the vertex colour, blended over the image by its alpha. ImGui's colours are encoded for an SDR
// window's UNORM image, so that image takes them as they are, and any other takes them decoded to linear and encoded
// as its format expects (Encoding, as Output.frag.hlsl does for the scene): as they are for an sRGB-typed image the GPU
// encodes, PQ on Rec. 2020 at paper white for an HDR10 image, scRGB (1.0 = 80 nits) at paper white for a half-float
// one. The GPU blends in the image's space, so translucent panes blend in sRGB code over an SDR image, as ImGui
// means them to, in PQ code over an HDR10 one, and in linear over the others, a touch lighter. OverlayVaryings and
// OverlayConstants must match Overlay.vert.hlsl's.

#include "Include/Color.hlsli"
#include "Include/Heap.hlsli"

// Nits the UI's white shows as on an HDR display, the same as the scene's 1.0 (Output.frag.hlsl's PaperWhiteNits).
static const float PaperWhiteNits = 200.0;

struct OverlayConstants
{
    float2 invTargetSize;
    uint vertices;
    uint firstVertex;
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

float4 main(OverlayVaryings input) : SV_Target0
{
    float4 color = TEXTURE(c.texture).Sample(SAMPLER_STATE(c.sampler), input.uv) * input.color;
    [branch] if (c.encoding == 1u)
    {
        return color;
    }

    float3 linearRgb = SrgbToLinear(color.rgb);
    [branch] if (c.encoding == 2u)
    {
        color.rgb = LinearToPq(Rec709ToRec2020(linearRgb) * PaperWhiteNits);
    }
    else if (c.encoding == 3u)
    {
        color.rgb = linearRgb * (PaperWhiteNits / 80.0);
    }
    else
    {
        color.rgb = linearRgb;
    }

    return color;
}
