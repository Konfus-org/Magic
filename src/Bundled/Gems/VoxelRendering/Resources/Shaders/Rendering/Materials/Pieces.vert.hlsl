// The vertex stage of every draw of pieces: no vertex buffer, each instance of the draw a piece of the view's (its
// instance's slot, which cluster and piece or card, and the piece's slot among the view's), from which each index of
// the draw is worked out: its triangle, index / 3, and corner, index % 3, of the piece's 32, the corner's vertex read
// from the geometry's words and moved by its instance, relative to the camera. A triangle past the cluster's last is put
// where no triangle is drawn. A mirrored instance's triangles wind the other way, so its second and third corners swap
// and the pipelines cull back faces alike. A card's six indices are its quad's, turned to face the camera
// (CardCorner). With PIECES_PLACE_ONLY, it hands on only where the triangle is and its name, for the bins that do not
// clip.

#ifndef PIECES_PLACE_ONLY
#define PIECES_PLACE_ONLY 0
#endif

#include "Include/PieceVaryings.hlsli"
#include "Rendering/Include/Materials.hlsli"
#include "Rendering/Include/Pieces.hlsli"

#if PIECES_PLACE_ONLY
PiecePlace main(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
#else
PieceVaryings main(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
#endif
{
    LoadView(Draw.view);

    // The draw's first instance is its bin's first piece, which the instance's number counts from.
    uint3 piece = BUFFER(Draw.pieces).Load3(instance * 12u);
    GpuInstance placed = LoadInstance(piece.x);
    uint face = vertex / 3u;
    uint corner = vertex % 3u;
    corner = (placed.flags & InstanceMirrored) != 0u && corner != 0u ? 3u - corner : corner;
    bool isCard = (piece.y & CardBit) != 0u;
    float3 position;
    float3 normal = float3(0.0, 1.0, 0.0);
    float4 tangent = float4(0.0, 0.0, 0.0, 0.0);
    float2 uv = float2(0.0, 0.0);
    float4 view = float4(0.0, 0.0, 0.0, 0.0);
    float3 front = float3(0.0, 0.0, 0.0);
    uint2 atlases = uint2(0xFFFFFFFFu, 0xFFFFFFFFu);
    uint slot;
    bool isPast = false;
    [branch] if (isCard)
    {
        GpuCard card = LoadCard(piece.y & ~CardBit);
        CardCorner(placed, card, CardCorners[min(face * 3u + corner, 5u)], position, normal, uv, view, front);
        atlases = uint2(card.surface, card.normals);
        slot = card.slot;
        isPast = face >= 2u;
    }
    else
    {
        GpuCluster cluster = LoadCluster(piece.y >> 2);
        uint index = (piece.y & 3u) * PieceTriangles + face;
        isPast = index >= ClusterTriangles(cluster);
        PieceVertex corners = LoadCorner(cluster, LoadTriangle(cluster, min(index, ClusterTriangles(cluster) - 1u))[corner]);
        position = InstancePosition(placed, corners.position);
        normal = InstanceNormal(placed, corners.normal);
        tangent = float4(NormalizeOrZero(InstanceDirection(placed, corners.tangent.xyz)), corners.tangent.w);
        uv = corners.uv;
        slot = ClusterSlot(cluster);
    }

    float4 clip = isPast ? float4(asfloat(0x7FC00000u).xxx, 1.0) : ToClip(position);
    uint id = ((piece.z << 5) | face) + 1u;
#if PIECES_PLACE_ONLY
    PiecePlace output;
    output.id = id;
    output.clip = clip;
    return output;
#else
    GpuModel model = LoadModel(placed.model);
    float takeover = CardTakeover(model, placed, length(InstancePosition(placed, model.bounds.xyz)));
    PieceVaryings output;
    output.position = position;
    output.normal = normal;
    output.tangent = tangent;
    output.uv = uv;
    output.material = InstanceMaterial(placed, slot);
    output.flags = placed.flags;
    output.card = view;
    output.dissolve = DissolveOf(placed, takeover, isCard);
    output.front = front;
    output.atlases = atlases;
    output.id = id;
    output.clip = clip;
    return output;
#endif
}
