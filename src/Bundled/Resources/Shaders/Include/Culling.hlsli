// What a renderer drawing clusters culls with, shared by its culling passes (Shaders/Clusters/): where each count lives
// in CullCounts, the rows of CullArgs each indirect pass dispatches by, the raster bins, the distance buckets detail is
// shared out by, and the test against the depth pyramid (HiZ). Each view culls in two phases (Karis 2021; Haar &
// Aaltonen 2015): first what last frame's pyramid, seen through last frame's view, says is in sight, drawn; then a
// pyramid of what was drawn, which everything the first phase hid is tested again against, so what came into sight is
// drawn the same frame, and what was wrongly hidden by a stale pyramid is too.

#ifndef MAGIC_CULLING_HLSLI
#define MAGIC_CULLING_HLSLI

#include "Include/Pieces.hlsli"

// CullCounts, a uint each.
static const uint CountJobs = 0u;           // jobs written, both phases
static const uint CountJobsFirst = 1u;      // of those, the first phase's
static const uint CountClusters = 2u;       // visible clusters written, both phases
static const uint CountClustersFirst = 3u;  // of those, the first phase's
static const uint CountHiddenInstances = 4u;
static const uint CountHiddenClusters = 5u;
static const uint CountPieces = 6u;         // pieces placed, both phases
static const uint CountPiecesFirst = 7u;    // of those, the first phase's

// CullArgs, three uints each: what each indirect pass dispatches by.
static const uint ArgsClusterCull = 0u;
static const uint ArgsPieceWrite = 1u;
static const uint ArgsInstanceCullSecond = 2u;
static const uint ArgsClusterRetest = 3u;
static const uint ArgsClusterCullSecond = 4u;
static const uint ArgsPieceWriteSecond = 5u;

// Raster bins: the two that do not clip, single-sided then double-sided; a surface's own clusters' from 2, and its
// cards' CardBinOffset after (the renderer's GpuCache.cs). BinArgs holds a phase's 256 after another's.
static const uint RasterBins = 256u;
static const uint FixedBins = 2u;
static const uint CardBinOffset = 126u;

// A material row's MaterialBins word, as the renderer's GpuCache.cs writes it: its raster bin in the low byte, its
// resolve bin in the third, and these.
static const uint BinMasked = 1u << 8;
static const uint BinDoubleSided = 1u << 9;
static const uint BinTransparent = 1u << 10;
static const uint BinReady = 1u << 11;

// Clusters a culling group of 64 threads tests, one each: a job.
static const uint JobClusters = 64u;

// Groups an indirect dispatch runs along x at most, as every API allows; more go on in y (Args.comp.hlsl).
static const uint MaxGroupsX = 65535u;

// A group's number among all of an indirect dispatch's, which run MaxGroupsX to a row.
uint LinearGroup(uint3 group)
{
    return group.y * MaxGroupsX + group.x;
}

// Buckets the distance from the player's camera is cut into, four to a doubling, which detail is shared out by
// nearest first when the pieces would not fit (Budget in CullBegin).
static const uint Buckets = 64u;

uint BucketOfDistance(float distance)
{
    return min((uint)(log2(max(distance, 1.0)) * 4.0), Buckets - 1u);
}

uint BucketOf(float3 centre)
{
    return BucketOfDistance(length(centre - PriorityPosition));
}

// How many texels a view's depth pyramid's level is across and down, and where it starts in HiZ: level 0 is the depth
// halved, each after it halved again, rounding up, down to one texel, each stored after the one before.
void HiZLevel(uint level, out uint2 size, out uint offset)
{
    size = (uint2)ViewSize;
    offset = 0u;
    [loop] for (uint at = 0u; at <= level; at++)
    {
        size = max((size + 1u) / 2u, uint2(1u, 1u));
        offset += at < level ? size.x * size.y : 0u;
    }
}

#if defined(HAS_HiZ)
// The depth a level of size texels starting at offset holds at a texel, the level's last past its edge.
float HiZAt(uint2 size, uint offset, uint2 texel)
{
    texel = min(texel, size - 1u);
    return asfloat(HiZ.Load((offset + texel.y * size.x + texel.x) * 4u));
}

// Whether a sphere is hidden behind what the pyramid holds: its rectangle on screen read at the level where it spans two
// texels at most, and its nearest point farther than the farthest depth there. clip is its centre through a
// view-projection whose w is the view depth (this frame's or the last's); the depth is reverse-Z, so farther is less.
bool IsHidden(float4 clip, float radius)
{
    if (clip.w - radius <= Near)
    {
        return false;
    }

    float2 centre = clip.xy / clip.w;
    float2 extent = radius / (clip.w - radius) * ProjScale;
    float2 low = float2(centre.x - extent.x, -(centre.y + extent.y)) * 0.5 + 0.5;
    float2 high = float2(centre.x + extent.x, -(centre.y - extent.y)) * 0.5 + 0.5;
    if (any(high < 0.0) || any(low > 1.0))
    {
        return false;
    }

    // The rectangle in level 0's texels, each two pixels a side, then the level at which it is two texels at most.
    float2 texels = ViewSize * 0.5;
    float2 first = saturate(low) * texels;
    float2 last = saturate(high) * texels;
    float span = max(last.x - first.x, last.y - first.y);
    uint levels = firstbithigh(max((uint)max(ViewSize.x, ViewSize.y), 1u)) + 1u;
    uint level = min((uint)ceil(log2(max(span, 1.0))), levels - 1u);
    uint2 from = (uint2)(first / (float)(1u << level));
    uint2 to = (uint2)(last / (float)(1u << level));
    to = min(to, from + 1u);
    uint2 size;
    uint offset;
    HiZLevel(level, size, offset);
    float farthest = min(min(HiZAt(size, offset, from), HiZAt(size, offset, uint2(to.x, from.y))), min(HiZAt(size, offset, uint2(from.x, to.y)), HiZAt(size, offset, to)));
    float nearest = Near / (clip.w - radius);
    return nearest < farthest;
}
#endif

// The raster bin a cluster of a material row is drawn in: its surface's own when it clips (a masked material, or a
// piece dissolving), else the single- or double-sided bin that does not; a card's, its surface's card bin.
uint BinOf(uint bins, bool dissolves, bool isCard)
{
    uint own = bins & 0xFFu;
    if (isCard)
    {
        return own + CardBinOffset;
    }

    bool clips = (bins & BinMasked) != 0u || dissolves;
    return clips ? own : ((bins & BinDoubleSided) != 0u ? 1u : 0u);
}

// Whether a material row is in and drawn by the visibility buffer: not still loading, nor transparent.
bool IsDrawnOpaque(uint bins)
{
    return (bins & BinReady) != 0u && (bins & BinTransparent) == 0u;
}

#endif
