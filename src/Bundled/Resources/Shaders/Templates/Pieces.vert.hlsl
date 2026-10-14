// The vertex stage of every draw of pieces: no vertex buffer, only an instance-rate buffer of pieces (an instance's
// slot, which cluster and piece or card, and the piece's slot among the view's), from which each index of the draw is
// worked out: its triangle, index / 3, and corner, index % 3, of the piece's 32, the corner's vertex read from the
// geometry's words and moved by its instance, relative to the camera. A triangle past the cluster's last is put where no
// triangle is drawn. A mirrored instance's triangles wind the other way, so its second and third corners swap and the
// pipelines cull back faces alike. A card's six indices are its quad's, turned to face the camera (CardCorner). With
// PIECES_PLACE_ONLY, it hands on only where the triangle is and its name, for the bins that do not clip.

#ifndef PIECES_PLACE_ONLY
#define PIECES_PLACE_ONLY 0
#endif

#include "Include/Bindings.hlsli"

#define HAS_Instances 1
#define HAS_Models 1
#define HAS_Clusters 1
#define HAS_Cards 1
#define HAS_Geometry 1
ByteAddressBuffer Instances : READ(0);
ByteAddressBuffer Models : READ(1);
ByteAddressBuffer Clusters : READ(2);
ByteAddressBuffer Cards : READ(3);
ByteAddressBuffer Geometry : READ(4);

#include "Include/PieceVaryings.hlsli"
#include "Include/Pieces.hlsli"

struct VsIn
{
    uint instance : TEXCOORD0;
    uint what : TEXCOORD1;
    uint slot : TEXCOORD2;
    uint vertex : SV_VertexID;
};

#if PIECES_PLACE_ONLY
PiecePlace main(VsIn input)
#else
PieceVaryings main(VsIn input)
#endif
{
    GpuSceneInstance instance = LOAD(Instances, GpuSceneInstance, input.instance);
    uint face = input.vertex / 3u;
    uint corner = input.vertex % 3u;
    corner = (instance.flags & SceneMirrored) != 0u && corner != 0u ? 3u - corner : corner;
    bool isCard = (input.what & CardBit) != 0u;
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
        GpuCard card = LOAD(Cards, GpuCard, input.what & ~CardBit);
        CardCorner(instance, card, CardCorners[min(face * 3u + corner, 5u)], position, normal, uv, view, front);
        atlases = uint2(card.surface, card.normals);
        slot = card.slot;
        isPast = face >= 2u;
    }
    else
    {
        GpuCluster cluster = LOAD(Clusters, GpuCluster, input.what >> 2);
        uint index = (input.what & 3u) * PieceTriangles + face;
        isPast = index >= ClusterTriangles(cluster);
        PieceVertex vertex = LoadCorner(cluster, LoadTriangle(cluster, min(index, ClusterTriangles(cluster) - 1u))[corner], cluster.step);
        position = ScenePosition(instance, vertex.position);
        normal = SceneNormal(instance, vertex.normal);
        tangent = float4(NormalizeOrZero(SceneDirection(instance, vertex.tangent.xyz)), vertex.tangent.w);
        uv = vertex.uv;
        slot = ClusterSlot(cluster);
    }

    float4 clip = isPast ? float4(asfloat(0x7FC00000u).xxx, 1.0) : mul(ViewProj, float4(position, 1.0));
    uint id = ((input.slot << 5) | face) + 1u;
    GpuModel model = LOAD(Models, GpuModel, instance.model);
#if PIECES_PLACE_ONLY
    // The models stay bound as every draw of pieces binds them, though only a piece that dissolves reads them: a flag no
    // model has keeps the compiler from dropping them, and every binding after them moving.
    PiecePlace output;
    output.id = id + (model.padding.x == 0xFFFFFFFFu ? 1u : 0u);
    output.clip = clip;
    return output;
#else
    float takeover = CardTakeover(model, instance, length(ScenePosition(instance, model.bounds.xyz)));
    PieceVaryings output;
    output.position = position;
    output.normal = normal;
    output.tangent = tangent;
    output.uv = uv;
    output.material = SceneMaterial(instance, slot);
    output.flags = instance.flags;
    output.card = view;
    output.dissolve = DissolveOf(instance, takeover, isCard);
    output.front = front;
    output.atlases = atlases;
    output.id = id;
    output.clip = clip;
    return output;
#endif
}
