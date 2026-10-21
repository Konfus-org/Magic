// Draws the shadows' pieces into the pool, an instance a piece (ShadowPieces: an instance's slot, a cluster's row times
// four plus which piece, and the view drawing it) and 96 indices its triangles, as the views' pieces are drawn
// (Templates/Pieces.vert.hlsl): each corner read from the geometry, moved by its instance relative to the camera, taken
// onto its view's page by the view's rows, then onto the page's square of the pool, and clipped to the rectangle of it
// the view draws, so a caster reaching past it draws nothing into its neighbours or over what stands. Depth is not
// clipped, so a caster nearer the key light than the page's depth reaches is pressed flat against it and still casts; a
// light's face is clipped LightNear ahead of it instead, as a corner on the light itself would be on every edge of its
// page at once, kept, and stretched across the whole pool.

#define HAS_Geometry 1
#include "Include/Pieces.hlsli"
#include "Include/VirtualShadows.hlsli"

struct VsIn
{
    uint instance : TEXCOORD0;
    uint what : TEXCOORD1;
    uint view : TEXCOORD2;
    uint vertex : SV_VertexID;
};

struct VsOut
{
    float4 clip : SV_Position;
    float4 inside : SV_ClipDistance0;
    float ahead : SV_ClipDistance1;
};

VsOut main(VsIn input)
{
    GpuInstance instance = LOAD(Instances, GpuInstance, input.instance);
    GpuCluster cluster = LOAD(Clusters, GpuCluster, input.what >> 2);
    uint index = (input.what & 3u) * PieceTriangles + input.vertex / 3u;
    uint triangles = ClusterTriangles(cluster);
    PieceVertex vertex = LoadCorner(cluster, LoadTriangle(cluster, min(index, triangles - 1u))[input.vertex % 3u], cluster.step);
    float4 position = float4(InstancePosition(instance, vertex.position), 1.0);

    GpuShadowView view = LOAD(ShadowViews, GpuShadowView, input.view);
    float4 page = float4(dot(view.row0, position), dot(view.row1, position), dot(view.row2, position), dot(view.row3, position));
    GpuClipmap clipmap = ClipmapOf();
    float size = (view.page & PageSmall) != 0u ? (float)SmallTexels : (float)PageTexels;
    float pool = (float)(clipmap.poolAcross * PageTexels);
    float2 corner = (float2)PageCorner(view.page, clipmap);
    float scale = size / pool;
    float2 offset = float2((2.0 * corner.x + size) / pool - 1.0, 1.0 - (2.0 * corner.y + size) / pool);

    VsOut output;
    output.clip = index >= triangles
        ? float4(asfloat(0x7FC00000u).xxx, 1.0)
        : float4(page.xy * scale + page.w * offset, page.z, page.w);
    float2 low;
    float2 high;
    RectClip(view.rect, low, high);
    output.inside = float4(page.w * high.x - page.x, page.x - page.w * low.x, page.w * high.y - page.y, page.y - page.w * low.y);
    output.ahead = (view.group & LightOwner) != 0u ? page.w - LightNear : 1.0;
    return output;
}
