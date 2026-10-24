// A view's depth pyramid (hiZ), which culling tests what it draws against: level 0 is the view's depth halved, each
// level after it halved again, rounding up, down to one texel, each texel the farthest of the four under it, which,
// reverse-Z, is the least, and each level stored after the one before. A level whose size is odd covers its last row or
// column with the texel before it, so nothing is left out. IsHidden tests a sphere against it.
//
// BuildDepthPyramid builds every level in one dispatch (as AMD's single-pass downsampler does): each group of 16 x 16
// lanes takes 64 x 64 pixels of depth down to levels 0 to 5, a level a step, through groupshared memory, and the last
// group to finish (Core/Compute's IsLastGroup) builds the levels above from every group's level 5, which the groups
// write and the last reads through atomics.

module "Renderer/DepthPyramid";

import Core.Compute;
__exported import Renderer.World;

struct DepthPyramidConstants
{
    TextureRef depth;        // the view's depth
    ArrayRef<float> hiZ;
    ArrayRef<uint> counters; // where the groups count themselves, at GroupsDone
    uint levels;             // the levels the pyramid has
}

// Where in counters the groups count themselves: the culling's counts, past the culling's own (Renderer/Culling).
static const uint GroupsDone = 8u;

// The levels a group builds itself, 0 to 5, and its lanes across and down, one a texel of level 1.
static const uint GroupLevels = 6u;
static const uint GroupSize = 16u;

groupshared float Tile[GroupSize][GroupSize];

// How many texels a level of the pyramid is across and down, and where it starts in hiZ.
public struct HiZLevel
{
    uint2 size;
    uint offset;
}

public HiZLevel GetHiZLevel(uint level)
{
    HiZLevel found;
    found.size = (uint2)View.viewSize;
    found.offset = 0u;
    [loop] for (uint at = 0u; at <= level; at++)
    {
        found.size = max((found.size + 1u) / 2u, uint2(1u, 1u));
        found.offset += at < level ? found.size.x * found.size.y : 0u;
    }

    return found;
}

// The depth a level holds at a texel, the level's last past its edge.
public float LoadHiZ(ArrayRef<float> hiZ, HiZLevel level, uint2 texel)
{
    texel = min(texel, level.size - 1u);
    return hiZ[level.offset + texel.y * level.size.x + texel.x];
}

// Whether a sphere is hidden behind what the pyramid holds: its rectangle on screen read at the level where it spans
// two texels at most, and its nearest point farther than the farthest depth there. clip is its centre through a
// view-projection whose w is the view depth (this frame's or the last's); the depth is reverse-Z, so farther is less.
public bool IsHidden(ArrayRef<float> hiZ, float4 clip, float radius)
{
    if (clip.w - radius <= View.near)
    {
        return false;
    }

    float2 centre = clip.xy / clip.w;
    float2 extent = radius / (clip.w - radius) * View.projScale;
    float2 low = float2(centre.x - extent.x, -(centre.y + extent.y)) * 0.5 + 0.5;
    float2 high = float2(centre.x + extent.x, -(centre.y - extent.y)) * 0.5 + 0.5;
    if (any(high < 0.0) || any(low > 1.0))
    {
        return false;
    }

    // The rectangle in level 0's texels, each two pixels a side, then the level at which it is two texels at most.
    float2 texels = View.viewSize * 0.5;
    float2 first = saturate(low) * texels;
    float2 last = saturate(high) * texels;
    float span = max(last.x - first.x, last.y - first.y);
    uint levels = firstbithigh(max((uint)max(View.viewSize.x, View.viewSize.y), 1u)) + 1u;
    uint level = min((uint)ceil(log2(max(span, 1.0))), levels - 1u);
    uint2 from = (uint2)(first / (float)(1u << level));
    uint2 to = (uint2)(last / (float)(1u << level));
    to = min(to, from + 1u);
    HiZLevel read = GetHiZLevel(level);
    float farthest = min(
        min(LoadHiZ(hiZ, read, from), LoadHiZ(hiZ, read, uint2(to.x, from.y))),
        min(LoadHiZ(hiZ, read, uint2(from.x, to.y)), LoadHiZ(hiZ, read, to)));
    float nearest = View.near / (clip.w - radius);
    return nearest < farthest;
}

// The farthest of the view's depth over the four pixels under a texel of level 0, the last pixel past the view's edge.
float GetFarthestPixel(TextureRef depth, uint2 texel)
{
    int2 last = int2(View.viewSize) - 1;
    int2 at = int2(texel * 2u);
    return min(
        min(depth.Load(min(at, last), 0).r, depth.Load(min(at + int2(1, 0), last), 0).r),
        min(depth.Load(min(at + int2(0, 1), last), 0).r, depth.Load(min(at + 1, last), 0).r));
}

// The texel of a level that a corner of a texel of the level above stands on, the level's last past its edge.
uint2 GetUnder(uint2 texel, uint corner, HiZLevel below)
{
    return min(texel * 2u + uint2(corner & 1u, corner >> 1u), below.size - 1u);
}

[shader("compute")]
[numthreads(16, 16, 1)]
void BuildDepthPyramid(
    uint3 group : SV_GroupID,
    uint3 thread : SV_GroupThreadID,
    uint index : SV_GroupIndex,
    uniform PassHeader pass,
    uniform DepthPyramidConstants c)
{
    BeginPass(pass);
    ArrayRef<uint> bits = { c.hiZ.id };

    // Level 1, a texel a lane, from its four texels of level 0, which are written as they are worked out. A lane past
    // the level's edge works nothing out: no texel within it reads one past it.
    HiZLevel level0 = GetHiZLevel(0u);
    HiZLevel level1 = GetHiZLevel(1u);
    uint2 own = group.xy * GroupSize + thread.xy;
    bool isInside = all(own < level1.size);
    float farthest = 0.0;
    if (isInside)
    {
        [unroll] for (uint corner = 0u; corner < 4u; corner++)
        {
            uint2 under = GetUnder(own, corner, level0);
            float depth = GetFarthestPixel(c.depth, under);
            if (all(under == own * 2u + uint2(corner & 1u, corner >> 1u)))
            {
                c.hiZ[level0.offset + under.y * level0.size.x + under.x] = depth;
            }

            farthest = corner == 0u ? depth : min(farthest, depth);
        }

        if (c.levels > 1u)
        {
            c.hiZ[level1.offset + own.y * level1.size.x + own.x] = farthest;
        }
    }

    Tile[thread.y][thread.x] = farthest;

    // Levels 2 to 5, each from the one below in the tile, fewer lanes a level.
    [unroll] for (uint level = 2u; level < GroupLevels; level++)
    {
        uint size = GroupSize >> (level - 1u);
        HiZLevel below = GetHiZLevel(level - 1u);
        HiZLevel here = GetHiZLevel(level);
        uint2 at = group.xy * size + thread.xy;
        bool isBuilt = all(thread.xy < size) && all(at < here.size);
        uint2 origin = group.xy * size * 2u;
        GroupMemoryBarrierWithGroupSync();
        float built = 0.0;
        if (isBuilt)
        {
            [unroll] for (uint corner = 0u; corner < 4u; corner++)
            {
                uint2 under = GetUnder(at, corner, below) - origin;
                built = corner == 0u ? Tile[under.y][under.x] : min(built, Tile[under.y][under.x]);
            }
        }

        GroupMemoryBarrierWithGroupSync();
        if (isBuilt)
        {
            Tile[thread.y][thread.x] = built;
            uint texel = here.offset + at.y * here.size.x + at.x;
            if (level == GroupLevels - 1u && level < c.levels)
            {
                bits.StoreAtomic(texel, asuint(built));
            }
            else if (level < c.levels)
            {
                c.hiZ[texel] = built;
            }
        }
    }

    // The levels above 5, by the last group, a level at a time.
    if (!IsLastGroup(c.counters, GroupsDone, index))
    {
        return;
    }

    [loop] for (uint level = GroupLevels; level < c.levels; level++)
    {
        HiZLevel below = GetHiZLevel(level - 1u);
        HiZLevel here = GetHiZLevel(level);
        [loop] for (uint texel = index; texel < here.size.x * here.size.y; texel += GroupSize * GroupSize)
        {
            uint2 at = uint2(texel % here.size.x, texel / here.size.x);
            float built = 0.0;
            [unroll] for (uint corner = 0u; corner < 4u; corner++)
            {
                uint2 under = GetUnder(at, corner, below);
                float depth = asfloat(bits.LoadAtomic(below.offset + under.y * below.size.x + under.x));
                built = corner == 0u ? depth : min(built, depth);
            }

            bits.StoreAtomic(here.offset + texel, asuint(built));
        }

        DeviceMemoryBarrierWithGroupSync();
    }
}
