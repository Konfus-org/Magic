// The scene as a renderer drawing clusters keeps it (its ClusterPool.cs and Scene.cs), read back: a
// cluster's vertices and triangles from the geometry's words, a card's quad, and how an instance dissolves. What reads
// the geometry is there when whoever includes it declares Geometry (HAS_Geometry), as a pass's header does for what
// the pass reads: a shader binds only what it uses.
//
// A piece is up to 32 triangles of a cluster, or a card: the unit the visibility buffer is drawn in, an instance of one
// draw of 96 indices (six for a card), whose vertex stage finds its triangle and corner from the index alone. A piece's
// three words are its instance's slot, what it draws (its cluster's row times four plus which piece, or a card's row
// with CardBit), and its slot among the view's pieces, which the visibility buffer names a pixel's triangle by:
// ((slot << 5) | triangle) + 1, so 0 is nothing drawn.

#ifndef MAGIC_PIECES_HLSLI
#define MAGIC_PIECES_HLSLI

#include "Include/Math.hlsli"
#include "Include/Noise.hlsli"
#include "Include/Packing.hlsli"
#include "Include/Structs.hlsli"

static const uint PieceTriangles = 32u;
static const uint CardBit = 0x80000000u;

// Seconds a fade in takes.
static const float FadeSeconds = 0.4;

// The share either side of the distance a model's cards take over from its clusters over which both are drawn, each
// dissolving into the other.
static const float CardBand = 0.1;

// The views a card's atlases hold, as Core's Card.Views.
static const float CardViews = 8.0;

// A card's six indices: its four corners as two triangles, as Core's card meshes wind them.
static const uint CardCorners[6] = { 0u, 3u, 2u, 0u, 2u, 1u };
static const float2 CardCornerUv[4] = { float2(-1.0, -1.0), float2(1.0, -1.0), float2(1.0, 1.0), float2(-1.0, 1.0) };

struct PieceVertex
{
    float3 position;
    float3 normal;
    float4 tangent;
    float2 uv;
};

uint ClusterTriangles(GpuCluster cluster)
{
    return cluster.counts & 0xFFu;
}

uint ClusterSlot(GpuCluster cluster)
{
    return (cluster.counts >> 17) & 7u;
}

uint ClusterLevel(GpuCluster cluster)
{
    return (cluster.counts >> 20) & 31u;
}

uint ClusterPieces(GpuCluster cluster)
{
    return (ClusterTriangles(cluster) + PieceTriangles - 1u) / PieceTriangles;
}

int SignExtend20(uint value)
{
    return ((int)(value << 12)) >> 12;
}

#if defined(HAS_Geometry)
// A vertex from its five words (ClusterPool.PackVertex): its position in steps of its model's grid, its normal and
// tangent on the octahedron, its uv as two halves.
PieceVertex LoadVertex(uint word, float step)
{
    uint4 packed = Geometry.Load4(word * 4u);
    uint uv = Geometry.Load((word + 4u) * 4u);
    PieceVertex vertex;
    int x = SignExtend20(packed.x & 0xFFFFFu);
    int y = SignExtend20((packed.x >> 20) | ((packed.y & 0xFFu) << 12));
    int z = SignExtend20((packed.y >> 8) & 0xFFFFFu);
    vertex.position = float3(x, y, z) * step;
    vertex.normal = OctDecode(float2(packed.z & 0xFFFFu, packed.z >> 16) * (2.0 / 65535.0) - 1.0);
    float2 folded = float2((packed.w & 0xFFFFu) * (2.0 / 65535.0) - 1.0, ((packed.w >> 16) & 0x7FFFu) * (2.0 / 32767.0) - 1.0);
    vertex.tangent = float4(OctDecode(folded), (packed.w >> 31) != 0u ? -1.0 : 1.0);
    vertex.uv = float2(f16tof32(uv & 0xFFFFu), f16tof32(uv >> 16));
    return vertex;
}

// A triangle's three corners, each its place among its cluster's vertices.
uint3 LoadTriangle(GpuCluster cluster, uint face)
{
    uint word = Geometry.Load((cluster.triangles + face) * 4u);
    return uint3(word & 0xFFu, (word >> 8) & 0xFFu, (word >> 16) & 0xFFu);
}

// A triangle's vertex, its corner's place turned into the geometry's word.
PieceVertex LoadCorner(GpuCluster cluster, uint corner, float step)
{
    return LoadVertex(cluster.vertices + corner * 5u, step);
}
#endif

// How far through its fade an instance is, 0 to 1.
float FadeProgress(GpuSceneInstance instance)
{
    return saturate((Time - instance.fadeStart) / FadeSeconds);
}

// How far a model's cards have taken over from its clusters at a distance, 0 to 1 across the band about the distance
// they start at (the model's own cardDistance at unit scale and a 60 degree view, scaled by the instance and the
// view's zoom); 0 for a model with no cards.
float CardTakeover(GpuModel model, GpuSceneInstance instance, float distance)
{
    if (model.cardCount == 0u || model.cardDistance <= 0.0)
    {
        return 0.0;
    }

    // A view of half height tanY sees as far as a 60 degree one sees distance * tan(30) / tanY.
    float tanY = ViewSize.y * 0.5 / PixelScale;
    float seen = distance * tanY / 0.57735027;
    float start = model.cardDistance * instance.maxScale;
    return saturate((seen / start - (1.0 - CardBand)) / (2.0 * CardBand));
}

// What of a piece is kept as it dissolves, as two thresholds of a pixel's dither: it is kept where the dither is under
// x and not under y. A fade in raises x; a card taking over raises x for the card and y for the clusters, so the two
// between them draw each pixel once.
float2 DissolveOf(GpuSceneInstance instance, float takeover, bool isCard)
{
    float2 dissolve = float2(1.0, 0.0);
    float progress = FadeProgress(instance);
    dissolve.x = (instance.flags & SceneFadeIn) != 0u ? progress : dissolve.x;
    dissolve.x = isCard ? min(dissolve.x, takeover) : dissolve.x;
    dissolve.y = isCard ? dissolve.y : max(dissolve.y, takeover);
    return dissolve;
}

// Whether a pixel of a dissolving piece is kept: 0 or more to keep it, less to clip it. The dither moves each frame,
// so a temporal upscale blends the two into each other.
float DissolveKeep(float2 dissolve, float2 pixel)
{
    float dither = InterleavedGradientNoise(pixel, FrameIndex);
    return min(dissolve.x - dither - 1e-4, dither - dissolve.y);
}

// Whether a piece dissolves at all, so it is drawn by its surface's raster bin, which clips.
bool Dissolves(float2 dissolve)
{
    return dissolve.x < 1.0 || dissolve.y > 0.0;
}

// One corner of a card's quad, which turns about +Y to face the camera at the front of its mesh, as wide and tall as
// the instance scales it: where it is relative to the camera, its normal, its corner in -1..1, the view it shows (its
// atlases' cell, its turn, whether it shows the mesh mirrored) and which way its front is and how far.
void CardCorner(GpuSceneInstance instance, GpuCard card, uint corner, out float3 position, out float3 normal, out float2 uv, out float4 view, out float3 front)
{
    float3 axisX = float3(instance.row0.x, instance.row1.x, instance.row2.x);
    float3 axisY = float3(instance.row0.y, instance.row1.y, instance.row2.y);
    float3 axisZ = float3(instance.row0.z, instance.row1.z, instance.row2.z);
    float3 centre = ScenePosition(instance, card.centre);
    bool isMirrored = (instance.flags & SceneMirrored) != 0u;
    bool showsMirrored = isMirrored != (card.halfWidth < 0.0);
    float turn = card.turn + atan2(axisZ.x, axisZ.z);

    // The camera is at the origin: the way to it along the ground is the centre's, back.
    float2 toCamera = -centre.xz;
    float2 side = dot(toCamera, toCamera) > 1e-8 ? toCamera * rsqrt(dot(toCamera, toCamera)) : float2(0.0, 1.0);
    float2 cornerUv = CardCornerUv[corner];
    float across = isMirrored ? -cornerUv.x : cornerUv.x;
    float width = abs(card.halfWidth) * max(length(axisX), length(axisZ));
    float height = card.halfHeight * length(axisY);
    position = centre + float3(side.x, 0.0, side.y) * width + float3(-side.y, 0.0, side.x) * (across * width) + float3(0.0, cornerUv.y * height, 0.0);

    // The camera's side turned back into the mesh's space, then mirrored back, is the view to show.
    float turnCos = cos(turn);
    float turnSin = sin(turn);
    float2 seen = float2(turnCos * side.x - turnSin * side.y, turnSin * side.x + turnCos * side.y);
    seen.x = showsMirrored ? -seen.x : seen.x;
    normal = float3(side.x, 0.0, side.y);
    uv = float2(showsMirrored ? -across : across, cornerUv.y);
    view = float4(frac(atan2(seen.x, seen.y) / TwoPi) * CardViews, turn, showsMirrored ? 1.0 : 0.0, 1.0);
    front = float3(side, width);
}

// Whether a sphere about a camera-relative centre is at least partly inside the view: in front of the near plane, past
// none of the four sides, and nearer than the far distance.
bool SphereInView(float3 centre, float radius)
{
    float3 seen = WorldToView(centre);
    float2 slopes = sqrt(ProjScale * ProjScale + 1.0);
    return seen.z + radius > Near
        && abs(seen.x) * ProjScale.x - seen.z <= radius * slopes.x
        && abs(seen.y) * ProjScale.y - seen.z <= radius * slopes.y
        && seen.z - radius < Far;
}

#endif
