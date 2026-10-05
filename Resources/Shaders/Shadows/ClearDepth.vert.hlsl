// Clears the tiles of the atlas that are drawn again this frame: six vertices per shadow view row from the vertex
// id alone, a quad over the row's tile at depth 0 (reverse-Z: nothing drawn) with the pipeline's depth test
// Always, and nothing (every vertex at one point) for a row that keeps its tile. There is no partial clear of a
// texture, and the atlas is loaded, not cleared, so the tiles that keep last frame's depth stay.

#include "Include/Frame.hlsli"
#include "Include/ShadowViews.hlsli"

// The corner of the quad each of the six vertices is: two triangles, (0, 1, 2) and (2, 1, 3).
static const uint CornerOfVertex[6] = { 0u, 1u, 2u, 2u, 1u, 3u };

StructuredBuffer<GpuShadowView> ShadowViews : READ(0);

float4 main(uint vertexId : SV_VertexID) : SV_Position
{
    GpuShadowView view = ShadowViews[vertexId / 6u];
    [branch] if (view.flags.y == 0u)
        return float4(0.0, 0.0, 0.0, 1.0);

    uint cornerIndex = CornerOfVertex[vertexId % 6u];
    float2 corner = float2((cornerIndex & 1u) != 0u ? 1.0 : 0.0, (cornerIndex & 2u) != 0u ? 1.0 : 0.0);
    float2 uv = view.tileUv.xy + corner * view.tileUv.zw;
    return float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.0, 1.0);
}
