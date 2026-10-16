// Empties the pages of shadow drawn this frame before their casters are, an instance a page (the view drawing it) and
// six indices its quad: the rectangle of the page's square of the pool the view draws, at the farthest depth, which the
// pass writes over whatever was there. Its instances are counted, not read: the draw steps through the shadows' pieces,
// as every draw of pieces steps through some.

#include "Include/VirtualShadows.hlsli"

static const float2 QuadCorners[6] = { float2(0.0, 0.0), float2(1.0, 0.0), float2(1.0, 1.0), float2(0.0, 0.0), float2(1.0, 1.0), float2(0.0, 1.0) };

struct VsIn
{
    uint unused0 : TEXCOORD0;
    uint unused1 : TEXCOORD1;
    uint unused2 : TEXCOORD2;
    uint vertex : SV_VertexID;
    uint view : SV_InstanceID;
};

float4 main(VsIn input) : SV_Position
{
    GpuShadowView view = LOAD(ShadowViews, GpuShadowView, input.view);
    GpuSunClipmap sun = SunClipmapOf();
    float size = (view.page & PageSmall) != 0u ? (float)SmallTexels : (float)PageTexels;
    float pool = (float)(sun.poolAcross * PageTexels);
    float4 rect = float4(view.rect & 0xFFu, (view.rect >> 8) & 0xFFu, (view.rect >> 16) & 0xFFu, view.rect >> 24) * (size / (float)PageTexels);
    float2 texel = (float2)PageCorner(view.page, sun) + lerp(rect.xy, rect.zw, QuadCorners[input.vertex % 6u]);
    return float4(texel.x / pool * 2.0 - 1.0, 1.0 - texel.y / pool * 2.0, 0.0, 1.0);
}
