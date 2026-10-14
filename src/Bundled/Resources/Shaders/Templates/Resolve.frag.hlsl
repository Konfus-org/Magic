// The G-buffer's resolve: composed after a surface and its LoadMaterialParams, drawn as a quad over each tile of the
// view its resolve bin was given, it shades each pixel of the tile its surface owns once. The pixel's triangle is the
// one the visibility buffer names: its piece, its instance, its cluster's three corners read from the geometry and
// moved as the vertex stage moved them (or a card's quad), and where the pixel falls on it, by the triangle's 2D
// homogeneous coordinates (Olano & Greer, "Triangle Scan Conversion using 2D Homogeneous Coordinates", 1997), which
// are perspective-correct and hold for a corner behind the camera. Its uv's derivatives are the same taken a pixel
// across and a pixel down, the neighbours' its own triangle would have had. Then the surface is evaluated as the
// rasteriser's fragments would have, and written as the G-buffer template writes it, with the pixel's velocity: where
// it was on screen last frame, its instance's last place through the last frame's view, less where it is now without
// this frame's jitter, in uv.

#include "Include/GBuffer.hlsli"
#include "Include/PieceFragment.hlsli"
#include "Include/Pieces.hlsli"
#include "Include/TileVaryings.hlsli"

struct ResolveOutput
{
    float4 albedo : SV_Target0;
    float4 normal : SV_Target1;
    float4 material : SV_Target2;
    float4 emissive : SV_Target3;
    float2 velocity : SV_Target4;
};

// Where a point of the view (ndc) falls on the triangle whose corners are a, b and c as clip (x, y, w): the weights of
// its corners, each the corner's share of the 2D homogeneous coordinates' inverse, normalised.
float3 BarycentricsAt(float3 a, float3 b, float3 c, float2 ndc)
{
    float3 at = float3(ndc, 1.0);
    float3 weights = float3(dot(cross(b, c), at), dot(cross(c, a), at), dot(cross(a, b), at));
    return weights / (weights.x + weights.y + weights.z);
}

ResolveOutput main(TileVaryings input)
{
    uint2 pixel = (uint2)input.clip.xy;
    uint id = Visibility.Load(int3(pixel, 0));
    if (id == 0u)
    {
        discard;
    }

    uint3 piece = VisiblePieces.Load3(((id - 1u) >> 5) * 12u);
    uint face = (id - 1u) & 31u;
    GpuSceneInstance instance = LOAD(Instances, GpuSceneInstance, piece.x);
    bool isCard = (piece.y & CardBit) != 0u;
    GpuCard card = LOAD(Cards, GpuCard, isCard ? piece.y & ~CardBit : 0u);
    GpuCluster cluster = LOAD(Clusters, GpuCluster, isCard ? 0u : piece.y >> 2);
    uint slot = isCard ? card.slot : ClusterSlot(cluster);
    uint material = SceneMaterial(instance, slot);
    if (((MaterialBins.Load(material * 4u) >> 16) & 0xFFu) != input.bin)
    {
        discard;
    }

    // The triangle's corners in the order the vertex stage drew them, a mirrored instance's second and third swapped.
    bool isMirrored = (instance.flags & SceneMirrored) != 0u;
    float3 positions[3];
    float3 normals[3];
    float4 tangents[3];
    float2 uvs[3];
    float3 places[3];
    float4 view = float4(0.0, 0.0, 0.0, 0.0);
    float3 front = float3(0.0, 0.0, 0.0);
    uint3 corners = isCard ? uint3(0u, 0u, 0u) : LoadTriangle(cluster, (piece.y & 3u) * PieceTriangles + face);
    [unroll] for (uint k = 0u; k < 3u; k++)
    {
        uint corner = isMirrored && k != 0u ? 3u - k : k;
        [branch] if (isCard)
        {
            CardCorner(instance, card, CardCorners[face * 3u + corner], positions[k], normals[k], uvs[k], view, front);
            tangents[k] = float4(0.0, 0.0, 0.0, 0.0);
            places[k] = card.centre;
        }
        else
        {
            PieceVertex vertex = LoadCorner(cluster, corners[corner], cluster.step);
            positions[k] = ScenePosition(instance, vertex.position);
            normals[k] = SceneNormal(instance, vertex.normal);
            tangents[k] = float4(NormalizeOrZero(SceneDirection(instance, vertex.tangent.xyz)), vertex.tangent.w);
            uvs[k] = vertex.uv;
            places[k] = vertex.position;
        }
    }

    float4 clip0 = mul(ViewProj, float4(positions[0], 1.0));
    float4 clip1 = mul(ViewProj, float4(positions[1], 1.0));
    float4 clip2 = mul(ViewProj, float4(positions[2], 1.0));
    float2 ndc = float2((pixel.x + 0.5) * ViewTexel.x * 2.0 - 1.0, 1.0 - (pixel.y + 0.5) * ViewTexel.y * 2.0);
    float3 here = BarycentricsAt(clip0.xyw, clip1.xyw, clip2.xyw, ndc);
    float3 across = BarycentricsAt(clip0.xyw, clip1.xyw, clip2.xyw, ndc + float2(2.0 * ViewTexel.x, 0.0));
    float3 down = BarycentricsAt(clip0.xyw, clip1.xyw, clip2.xyw, ndc - float2(0.0, 2.0 * ViewTexel.y));
    float2 uv = here.x * uvs[0] + here.y * uvs[1] + here.z * uvs[2];

    // Clockwise on screen is front, and the screen's y runs down where clip space's runs up: a front triangle's area in clip
    // space, counter-clockwise positive, is positive.
    float2 screen0 = clip0.xy / clip0.w;
    float2 screen1 = clip1.xy / clip1.w;
    float2 screen2 = clip2.xy / clip2.w;
    float2 side1 = screen1 - screen0;
    float2 side2 = screen2 - screen0;
    bool isFrontFace = (side1.x * side2.y - side1.y * side2.x) * clip0.w * clip1.w * clip2.w > 0.0;

    PieceVaryings varyings;
    varyings.position = here.x * positions[0] + here.y * positions[1] + here.z * positions[2];
    varyings.normal = here.x * normals[0] + here.y * normals[1] + here.z * normals[2];
    varyings.tangent = float4(here.x * tangents[0].xyz + here.y * tangents[1].xyz + here.z * tangents[2].xyz, tangents[0].w);
    varyings.uv = uv;
    varyings.material = material;
    varyings.flags = instance.flags;
    varyings.card = view;
    varyings.dissolve = float2(1.0, 0.0);
    varyings.front = front;
    varyings.atlases = uint2(card.surface, card.normals);
    varyings.id = id;
    varyings.clip = float4(pixel + 0.5, 0.0, 1.0);
    float2 uvDdx = (across.x * uvs[0] + across.y * uvs[1] + across.z * uvs[2]) - uv;
    float2 uvDdy = (down.x * uvs[0] + down.y * uvs[1] + down.z * uvs[2]) - uv;
    float cover;
    float depth;
    SurfaceInputs inputs = PieceInputsOf(varyings, uvDdx, uvDdy, isFrontFace, cover, depth);
    Surface surface = EvaluateSurface(inputs, LoadMaterialParams(material));

    GBufferSurface stored;
    stored.baseColor = surface.baseColor;
    stored.occlusion = surface.occlusion;
    stored.normal = NormalizeOrZero(surface.normal);
    stored.roughness = surface.roughness;
    stored.model = surface.shadingModel;
    stored.metallic = surface.metallic;
    stored.specular = surface.specular;
    stored.modelData = surface.modelData;
    GBufferOutput written = EncodeGBuffer(stored, surface.emissive + KeepBindingsAlive(instance.flags, uv));

    // A card moves as its centre does; a cluster's point as its instance moved it.
    float3 place = here.x * places[0] + here.y * places[1] + here.z * places[2];
    float3 last = isCard
        ? varyings.position + ScenePrevPosition(instance, card.centre) - ScenePosition(instance, card.centre)
        : ScenePrevPosition(instance, place);
    float4 lastClip = mul(PrevViewProj, float4(last, 1.0));
    float2 lastNdc = lastClip.xy / max(lastClip.w, 1e-6);

    ResolveOutput output;
    output.albedo = written.albedo;
    output.normal = written.normal;
    output.material = written.material;
    output.emissive = written.emissive;
    output.velocity = (ndc - Jitter - lastNdc) * float2(0.5, -0.5);
    return output;
}
