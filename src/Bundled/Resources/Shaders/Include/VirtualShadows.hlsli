// Virtual shadow maps, as a renderer drawing clusters keeps them (its Shadows.cs): every shadow is pages of one depth
// pool (ShadowPool), only those the views' pixels read, kept from frame to frame and drawn again where what casts into
// them changed. A page is 128 texels a side, a border of 4 round 120 that hold the shadow, so a filter at its edge
// reads its own border; the pool's last quarter is split into small pages, 32 texels with a border of 2.
//
// The key light's pages are a clipmap: levels about the camera, each twice as wide as the last and 64 pages a side, a
// level's window moving a page at a time, its entries addressed round by the page's place so a page that stays in the
// window keeps its entry. A point or spot light's pages are its six cube faces' at a mip (8, 4, 2 or 1 page a side, or
// one small page), found in a hash table by light, face, mip and page, made anew each frame from what is wanted,
// carrying its pages over from the last frame's table.
//
// The tables are one buffer (ShadowTables) of 16-byte rows: a record for each page of the pool, whole pages' first,
// then the clipmap's entries, then two tables of lights' entries, this frame's and the last's, which swap each frame;
// the clipmap (ShadowClipmap) says where each starts. An entry is four words: its pool page plus one (0 for none) with
// PageDirty (it waits to be drawn) and PageSmall (a small page); a clipmap entry's tag, the page it stands for, or a
// light entry's key, EmptyKey for none; the frame it was last wanted in; and the view drawing it this frame
// (ShadowViews), NoView for none. A page's record is what owns it, a clipmap entry plus one or a light entry with
// LightOwner, the frame it was last read in, so the pages read longest ago are the first taken for what is wanted now,
// and the rectangle of it waiting to be drawn again: all of it when new, else where what moved since it was drawn
// reaches, as only that can have changed, so a small caster moving in a big page draws little; and, only while the
// allocator gives pages out, what it was taken from, which lets go of it once all are given. The views read a page only
// where it is not waiting, which for a clipmap page waiting only where something moved is everywhere else on it, as the
// rest still holds what it did; elsewhere the next coarser, so nothing is ever unshadowed for waiting: a clipmap level
// past the last, or a light with none, is lit.

#ifndef MAGIC_VIRTUAL_SHADOWS_HLSLI
#define MAGIC_VIRTUAL_SHADOWS_HLSLI

#include "Include/Frame.hlsli"
#include "Include/Noise.hlsli"
#include "Include/Structs.hlsli"
#include "Lighting/Shadows.hlsli"

static const uint PageTexels = 128u;
static const uint PageBorder = 4u;
static const uint PageContent = 120u;
static const uint SmallTexels = 32u;
static const uint SmallBorder = 2u;
static const uint SmallContent = 28u;
static const uint ClipmapWindow = 64u;
static const uint ClipmapEntriesPerLevel = ClipmapWindow * ClipmapWindow;

// Levels the clipmap has at most, as the renderer's Shadows.MaxLevels.
static const uint ClipmapMaxLevels = 24u;
static const uint PageDirty = 0x80000000u;
static const uint PageSmall = 0x40000000u;
static const uint PageNumber = 0x3FFFFFFFu;
static const uint EmptyKey = 0xFFFFFFFFu;
static const uint NoView = 0xFFFFFFFFu;
static const uint LightOwner = 0x80000000u;
static const uint LightMips = 5u;
static const uint LightProbes = 32u;

// The nearest a light's faces see, in metres.
static const float LightNear = 0.05;

// Texels of a page a receiver is pushed out along its normal, so it does not shadow itself: the clipmap's, and a point
// or spot light's, whose faces' texels meet a receiver at steeper angles.
static const float ShadowBias = 1.5;
static const float LightShadowBias = 3.0;

// Pixels a texel of a point or spot light's face covers at most: two, as its shadow is soft, which wants a quarter of
// the pages one would, so many lights near the camera leave the pool room for the clipmap.
static const float LightTexelPixels = 2.0;

// Levels, as a share of a doubling, either side of where one clipmap level gives way to the next over which a pixel
// picks between them by dither (ClipmapLevelFor).
static const float ClipmapLevelBlend = 0.2;

// The clipmap levels past a pixel's own that it marks (VirtualShadows/ShadowMark.comp.hlsl) and its soft shadow is
// searched and filtered through, and no further: the page it reads is one it asked for, and its penumbra is at most a
// few of that level's texels, so the nearer the camera comes, the sharper the shadow. And the levels finer than its own
// it marks and may be filtered at where its penumbra is narrower than its own level's texels: a shadow's edge close up
// is then as sharp as half a pixel, where a level whose texels are a pixel wide would blur it over the filter's five.
static const uint ClipmapSoftLevels = 3u;
static const uint ClipmapFinerLevels = 1u;

// A view's ShadowArgs, by byte: the indirect draws of the pages' clear and of the shadows' pieces, five words each;
// whether the culls placed more jobs or pieces than there was room for, which would leave the pages drawn with them
// short of casters, so nothing is drawn and every page waits again (VirtualShadows/ShadowArgs.comp.hlsl); how many
// pages a frame may draw, halved after a frame that ran out of room and doubled back after one that did not, up to
// ShadowViews; and the indirect dispatches of the shadows' instance and cluster culls, three words each on rows of
// twelve bytes. Its ShadowCounts, apart, as no pass writes what it is dispatched by: how many pages are drawn, how many
// groups they are in, and the jobs and pieces the culls placed.
static const uint ShadowArgsClear = 0u;
static const uint ShadowArgsPieces = 20u;
static const uint ShadowArgsOverflowed = 40u;
static const uint ShadowArgsViewRoom = 44u;
static const uint ShadowArgsInstanceCull = 48u;
static const uint ShadowArgsClusterCull = 60u;
static const uint ShadowCountViews = 0u;
static const uint ShadowCountGroups = 4u;
static const uint ShadowCountJobs = 8u;
static const uint ShadowCountPieces = 12u;

// The key light's clipmap this frame (the renderer's GpuClipmap): its axes, how many levels, the frame's number,
// whether every page starts again, the main camera's depth along the light, how deep a page reaches either side, the
// finest level's width, the changed lights' bits' words, the pool's layout, how many spheres moved (Moved), where the
// main camera is, which the clipmap is placed about and the moved spheres are relative to, and where the tables start
// in ShadowTables and how many entries a light table has. Its levels follow it, from ClipmapLevelsAt.
struct GpuClipmap
{
    float3 right;
    uint levels;
    float3 up;
    uint frame;
    float3 direction;
    uint allDirty;
    float cameraDepth;
    float depthRange;
    float finest;
    uint changedLightWords;
    uint poolAcross;
    uint wholePages;
    uint smallPages;
    uint moved;
    float3 camera;
    uint levelTable;
    uint lightTable;
    uint lightTableLast;
    uint lightEntries;
    uint padding0;
    uint4 padding1;
};

static const uint ClipmapLevelsAt = 128u;

// One level of the clipmap: its window's first page, counted from the clipmap's origin, metres a page and a texel, and
// where the main camera is across the light from the window's corner.
struct GpuClipmapLevel
{
    int2 origin;
    float pageSize;
    float texel;
    float2 camera;
    float2 padding;
};

struct ShadowEntry
{
    uint page;
    uint tag;
    uint wanted;
    uint view;
};

// An entry of ShadowTables, read or written, by row.
#define TABLE_ENTRY(tables, row) (tables).Load<ShadowEntry>((row) * 16u)

// One page drawn this frame (Shaders/VirtualShadows/): what takes a camera-relative point onto the page as clip
// (its four rows, the page's whole 128 texels, border included, -1 to 1), its pool page as an entry holds it, its
// group, which culling finds its casters by, the rectangle of it drawn, which is all that is cleared and drawn, and the
// row of ShadowTables its entry is, which waits again should the frame run out of room for casters.
struct GpuShadowView
{
    float4 row0;
    float4 row1;
    float4 row2;
    float4 row3;
    uint page;
    uint group;
    uint rect;
    uint entry;
};

// What culling finds casters by: one clipmap page drawn, or the views of one light: the page's level, or the light's
// slot with LightOwner; the faces and mips the light's views are at as bits, face * LightMips + mip; the level's metres
// a texel; the clipmap page's view, NoView for a light; and where the light is, relative to the camera, and how far it
// reaches.
struct GpuShadowGroup
{
    uint target;
    uint faceMips;
    float texel;
    uint view;
    float3 light;
    float range;
};

// A point relative to this view's camera, relative to the main camera instead, which the clipmap is placed about.
float3 MainRelative(GpuClipmap clipmap, float3 position)
{
    return position + (CameraPosition - clipmap.camera);
}

#if defined(HAS_ShadowClipmap)
GpuClipmap ClipmapOf()
{
    return LOAD(ShadowClipmap, GpuClipmap, 0u);
}

GpuClipmapLevel ClipmapLevelOf(uint level)
{
    return ShadowClipmap.Load<GpuClipmapLevel>(ClipmapLevelsAt + level * 32u);
}
#endif

// The texels a side of a light's face at a mip: 960, 480, 240 and 120 in pages of 120, then one small page of 28.
uint FaceTexels(uint mip)
{
    return mip < 4u ? PageContent * (8u >> mip) : SmallContent;
}

uint FacePages(uint mip)
{
    return mip < 4u ? 8u >> mip : 1u;
}

// A pool page's record, as an entry names the page: whole pages' first, then small pages'.
uint RecordOf(GpuClipmap clipmap, uint page)
{
    uint index = (page & PageNumber) - 1u;
    return (page & PageSmall) != 0u ? clipmap.poolAcross * clipmap.poolAcross + index : index;
}

// A rectangle of a page, border and all, in 128ths of it, rounded out to its texels: its first column and row and one
// past its last, a byte each, columns rightward and rows downward from the page's top left; 0 for none.
static const uint WholeRect = (128u << 16) | (128u << 24);

uint UnionRect(uint a, uint b)
{
    if (a == 0u || b == 0u)
    {
        return a | b;
    }

    return min(a & 0xFFu, b & 0xFFu) | (min((a >> 8) & 0xFFu, (b >> 8) & 0xFFu) << 8)
        | (max((a >> 16) & 0xFFu, (b >> 16) & 0xFFu) << 16) | (max(a >> 24, b >> 24) << 24);
}

// The rectangle a span of a page of texels a side covers, 0 to 1 across it from its top left; none when it misses it.
uint RectOf(float2 low, float2 high, uint texels)
{
    if (any(high <= 0.0) || any(low >= 1.0))
    {
        return 0u;
    }

    uint2 first = (uint2)floor(saturate(low) * (float)texels);
    uint2 last = max((uint2)ceil(saturate(high) * (float)texels), first + 1u);
    uint step = PageTexels / texels;
    return (first.x * step) | ((first.y * step) << 8) | ((last.x * step) << 16) | ((last.y * step) << 24);
}

// A rectangle as clip on its page: x from low.x to high.x, y from low.y to high.y, y upward as the views' rows give it.
void RectClip(uint rect, out float2 low, out float2 high)
{
    float4 edges = float4(rect & 0xFFu, (rect >> 8) & 0xFFu, (rect >> 16) & 0xFFu, rect >> 24) / 64.0;
    low = float2(edges.x - 1.0, 1.0 - edges.w);
    high = float2(edges.z - 1.0, 1.0 - edges.y);
}

// Whether a sphere about a camera-relative point reaches into the rectangle a view draws: it is not wholly past any of
// the four planes where x or y is the rectangle's edge times w.
bool ReachesViewRect(GpuShadowView view, float3 centre, float radius)
{
    float2 low;
    float2 high;
    RectClip(view.rect, low, high);
    float4 planes[4] = { view.row0 - high.x * view.row3, low.x * view.row3 - view.row0, view.row1 - high.y * view.row3, low.y * view.row3 - view.row1 };
    float4 point4 = float4(centre, 1.0);
    [unroll] for (uint i = 0u; i < 4u; i++)
    {
        if (dot(planes[i], point4) > radius * length(planes[i].xyz))
        {
            return false;
        }
    }

    return true;
}


// The pool's texel a page's corner is at.
uint2 PageCorner(uint page, GpuClipmap clipmap)
{
    uint index = (page & PageNumber) - 1u;
    if ((page & PageSmall) != 0u)
    {
        uint whole = clipmap.wholePages + index / 16u;
        uint part = index % 16u;
        return uint2(whole % clipmap.poolAcross, whole / clipmap.poolAcross) * PageTexels + uint2(part % 4u, part / 4u) * SmallTexels;
    }

    return uint2(index % clipmap.poolAcross, index / clipmap.poolAcross) * PageTexels;
}

// The pool's texel at a place in a page's shadow, 0 to 1 across what it holds.
float2 PageTexel(uint page, float2 place, GpuClipmap clipmap)
{
    bool isSmall = (page & PageSmall) != 0u;
    return (float2)PageCorner(page, clipmap) + (isSmall ? SmallBorder : PageBorder) + saturate(place) * (isSmall ? SmallContent : PageContent);
}

bool HasPage(uint page)
{
    return (page & PageNumber) != 0u;
}

// Whether an entry's page can be read: there is one, and it is not waiting to be drawn.
bool IsDrawn(uint page)
{
    return HasPage(page) && (page & PageDirty) == 0u;
}


// The clipmap level whose texels are as fine as a pixel at a view depth: a pixel there covers viewDepth / PixelScale
// metres. Within ClipmapLevelBlend of a level's either side, a pixel picks between the two by dither, 0 to 1, more of
// it the coarser the nearer it is to that, so the levels blend into each other rather than meeting at a line; a dither
// of 0 gives the finest it ever picks there.
uint ClipmapLevelFor(GpuClipmap clipmap, float viewDepth, float dither)
{
    float finestTexel = clipmap.finest / (float)ClipmapWindow / (float)PageContent;
    float wanted = max(viewDepth / PixelScale / finestTexel, 1.0);
    float level = log2(wanted) + (dither * 2.0 - 1.0) * ClipmapLevelBlend;
    return min((uint)ceil(max(level, 0.0)), clipmap.levels - 1u);
}

// Which page of a clipmap level a camera-relative point is on, counted from the clipmap's origin, and where on it, 0 to
// 1; false when the point is outside the level's window.
bool ClipmapPageOf(GpuClipmap clipmap, GpuClipmapLevel level, float3 position, out int2 page, out float2 place)
{
    float3 main = MainRelative(clipmap, position);
    float2 across = float2(dot(main, clipmap.right), dot(main, clipmap.up)) + level.camera;
    float2 pages = across / level.pageSize;
    page = int2(0, 0);
    place = float2(0.0, 0.0);
    if (any(pages < 0.0) || any(pages >= (float)ClipmapWindow))
    {
        return false;
    }

    int2 local = (int2)floor(pages);
    page = level.origin + local;
    place = pages - (float2)local;
    return true;
}

// A clipmap page's entry, counted from the clipmap's first: by level, its place in the window addressed round.
uint ClipmapEntryOf(uint level, int2 page)
{
    uint2 wrapped = (uint2)(page & int(ClipmapWindow - 1u));
    return level * ClipmapEntriesPerLevel + wrapped.y * ClipmapWindow + wrapped.x;
}

uint ClipmapTagOf(int2 page)
{
    return ((uint)page.x & 0xFFFFu) | (((uint)page.y & 0xFFFFu) << 16);
}

// The page a clipmap entry stands for now, its level's window having moved: its place, counted from the clipmap's
// origin.
int2 ClipmapPageAt(GpuClipmapLevel level, uint entry)
{
    uint local = entry % ClipmapEntriesPerLevel;
    int2 wrapped = int2(local % ClipmapWindow, local / ClipmapWindow);
    int2 offset = (wrapped - (level.origin & int(ClipmapWindow - 1u)) + int(ClipmapWindow)) & int(ClipmapWindow - 1u);
    return level.origin + offset;
}

// A camera-relative point's depth from the light, reverse-Z: nearer the light is more.
float ClipmapDepthOf(GpuClipmap clipmap, float3 position)
{
    float along = dot(MainRelative(clipmap, position), clipmap.direction) + clipmap.cameraDepth;
    return 0.5 - along / (2.0 * clipmap.depthRange);
}

#if defined(HAS_ShadowClipmap)
// A camera-relative point pushed out along its normal by a texel of a clipmap level, as it looks itself up there.
float3 ClipmapMoved(GpuClipmap clipmap, uint level, float3 position, float3 normal)
{
    return NormalOffset(position, normal, -clipmap.direction, ClipmapLevelOf(min(level, clipmap.levels - 1u)).texel * ShadowBias);
}
#endif

// A cube face's axes: the way each looks, and up across it; right = cross(up, forward).
static const float3 FaceForwards[6] = { float3(1.0, 0.0, 0.0), float3(-1.0, 0.0, 0.0), float3(0.0, 1.0, 0.0), float3(0.0, -1.0, 0.0), float3(0.0, 0.0, 1.0), float3(0.0, 0.0, -1.0) };
static const float3 FaceUps[6] = { float3(0.0, 1.0, 0.0), float3(0.0, 1.0, 0.0), float3(0.0, 0.0, -1.0), float3(0.0, 0.0, 1.0), float3(0.0, 1.0, 0.0), float3(0.0, 1.0, 0.0) };

void FaceAxes(uint face, out float3 forward, out float3 right, out float3 up)
{
    forward = FaceForwards[face];
    up = FaceUps[face];
    right = cross(up, forward);
}

// Which face of a cube a direction from its centre is on: +X, -X, +Y, -Y, +Z, -Z.
uint FaceOf(float3 direction)
{
    float3 along = abs(direction);
    return along.x >= along.y && along.x >= along.z ? (direction.x >= 0.0 ? 0u : 1u)
        : along.y >= along.z ? (direction.y >= 0.0 ? 2u : 3u)
        : (direction.z >= 0.0 ? 4u : 5u);
}

// Where a point from a light falls on its face, 0 to 1 across it, and how far ahead of the light it is.
float2 FacePlaceOf(uint face, float3 fromLight, out float ahead)
{
    float3 forward;
    float3 right;
    float3 up;
    FaceAxes(face, forward, right, up);
    ahead = max(dot(fromLight, forward), 1e-6);
    float2 across = float2(dot(fromLight, right), dot(fromLight, up)) / ahead;
    return float2(across.x, -across.y) * 0.5 + 0.5;
}

// The coarsest mip of a light's face whose texels are as fine as LightTexelPixels pixels distance metres from the
// light, a pixel covering footprint metres: a face spans two of its tangent across, so it wants 2 distance / footprint
// texels a pixel.
uint LightMipFor(float footprint, float distance)
{
    float wanted = 2.0 * distance / max(footprint * LightTexelPixels, 1e-6);
    uint mip = 0u;
    [unroll] for (uint coarser = 1u; coarser < LightMips; coarser++)
    {
        mip = (float)FaceTexels(coarser) >= wanted ? coarser : mip;
    }

    return mip;
}

uint LightKeyOf(uint slot, uint face, uint mip, uint2 page)
{
    return (slot << 12) | (face << 9) | (mip << 6) | (page.y * 8u + page.x);
}

uint LightSlotOfKey(uint key)
{
    return key >> 12;
}

uint MipOfKey(uint key)
{
    return (key >> 6) & 7u;
}

// What takes a point relative to a light onto the page of one of its faces a key names: across and upward give its x
// and y, forward its w, the distance ahead of the face, and the page holds what has x and y within w.
void LightPageRows(uint key, out float3 across, out float3 upward, out float3 forward)
{
    uint mip = MipOfKey(key);
    uint2 page = uint2(key & 7u, (key >> 3) & 7u);
    float3 right;
    float3 up;
    FaceAxes((key >> 9) & 7u, forward, right, up);
    float side = (float)FacePages(mip);
    float content = mip < LightMips - 1u ? (float)PageContent / (float)PageTexels : (float)SmallContent / (float)SmallTexels;
    across = content * (side * right + (side - 2.0 * page.x - 1.0) * forward);
    upward = content * (side * up + (2.0 * page.y + 1.0 - side) * forward);
}

// The rectangle of the page a key names that a sphere about a point relative to its light covers, from the box round
// it seen from the light; all of it when the sphere reaches within LightNear of the light, none when it misses it.
uint LightPageRectOf(uint key, float3 centre, float radius)
{
    float3 across;
    float3 upward;
    float3 forward;
    LightPageRows(key, across, upward, forward);
    uint texels = MipOfKey(key) == LightMips - 1u ? SmallTexels : PageTexels;
    float ahead = dot(centre, forward);
    if (ahead + radius <= LightNear)
    {
        return 0u;
    }

    if (ahead - radius <= LightNear)
    {
        return WholeRect;
    }

    float2 at = float2(dot(centre, across), dot(centre, upward));
    float2 reach = radius * float2(length(across), length(upward));
    float2 low = min((at - reach) / (ahead - radius), (at - reach) / (ahead + radius));
    float2 high = max((at + reach) / (ahead - radius), (at + reach) / (ahead + radius));
    return RectOf(float2(low.x + 1.0, 1.0 - high.y) * 0.5, float2(high.x + 1.0, 1.0 - low.y) * 0.5, texels);
}

// The finest mip of a light group's faces' and mips' bits, face * LightMips + mip.
uint FinestMip(uint bits)
{
    uint mips = 0u;
    [unroll] for (uint face = 0u; face < 6u; face++)
    {
        mips |= (bits >> (face * LightMips)) & ((1u << LightMips) - 1u);
    }

    return firstbitlow(mips);
}

// A light's page's entry in a table of entries a power of two from row first of the tables, counted from first, by
// linear probing from its key's hash; NoView when it is not there. The tables read-only or not.
template<typename T>
uint FindLightEntry(T tables, uint first, uint key, uint entries)
{
    uint at = PcgHash(key) & (entries - 1u);
    [loop] for (uint probe = 0u; probe < LightProbes; probe++)
    {
        uint found = tables.Load((first + at) * 16u + 4u);
        if (found == key)
        {
            return at;
        }

        if (found == EmptyKey)
        {
            return NoView;
        }

        at = (at + 1u) & (entries - 1u);
    }

    return NoView;
}

#if defined(STAGE_COMPUTE)
// A light's page's entry, put in the first empty place its probe meets when it is not there; NoView when the table is
// too full to put it in. A place already holding the key is read, not swapped, as most callers find it there.
uint InsertLightEntry(RWByteAddressBuffer tables, uint first, uint key, uint entries)
{
    uint at = PcgHash(key) & (entries - 1u);
    [loop] for (uint probe = 0u; probe < LightProbes; probe++)
    {
        uint before = tables.Load((first + at) * 16u + 4u);
        if (before == EmptyKey)
        {
            tables.InterlockedCompareExchange((first + at) * 16u + 4u, EmptyKey, key, before);
        }

        if (before == EmptyKey || before == key)
        {
            return at;
        }

        at = (at + 1u) & (entries - 1u);
    }

    return NoView;
}
#endif

#if defined(HAS_ShadowPool) && defined(HAS_ShadowTables) && defined(HAS_ShadowClipmap)
float PoolSize()
{
    float width;
    float height;
    ShadowPool.GetDimensions(width, height);
    return width;
}

// The depth a page holds at a place on it, unfiltered.
float PageDepthAt(uint page, float2 place, GpuClipmap clipmap)
{
    return ShadowPool.Load(int3(PageTexel(page, place, clipmap), 0));
}

// The share of light reaching a receiver through a page: a 5 x 5 tent of comparisons (Lighting/Shadows.hlsli), which
// the page's border keeps inside it.
float PageShadow(uint page, float2 place, float depth, GpuClipmap clipmap)
{
    float size = PoolSize();
    return ShadowPcf(ShadowPool, ShadowPoolSampler, PageTexel(page, place, clipmap) / size, depth, float2(size, size));
}

// Whether an entry's page can be read at a place on it, 0 to 1 across what it holds: there is one, and it is not
// waiting to be drawn there. A page waiting only where something moved, its record's rectangle, is read everywhere
// further from that than the filter reaches, as the rest of it still holds what it did; one waiting whole, new or
// started again, is not.
bool IsReadable(GpuClipmap clipmap, uint page, float2 place)
{
    if (!HasPage(page))
    {
        return false;
    }

    if ((page & PageDirty) == 0u)
    {
        return true;
    }

    uint rect = ShadowTables.Load(RecordOf(clipmap, page) * 16u + 8u);
    if (rect == 0u || rect == WholeRect)
    {
        return false;
    }

    // The place in 128ths of the page, border and all, and the 5 x 5 tent's reach either side of it.
    float2 texel = (float)PageBorder + place * (float)PageContent;
    float reach = 3.0;
    return texel.x + reach < (float)(rect & 0xFFu) || texel.x - reach >= (float)((rect >> 16) & 0xFFu)
        || texel.y + reach < (float)((rect >> 8) & 0xFFu) || texel.y - reach >= (float)(rect >> 24);
}

// The clipmap level at or past level that holds a page readable at a point: its entry's page, and where on it; false
// past the last level.
bool ClipmapDrawnAt(GpuClipmap clipmap, inout uint level, float3 position, out uint page, out float2 place)
{
    page = 0u;
    place = float2(0.0, 0.0);
    [loop] for (; level < clipmap.levels; level++)
    {
        int2 at;
        if (!ClipmapPageOf(clipmap, ClipmapLevelOf(level), position, at, place))
        {
            continue;
        }

        ShadowEntry entry = TABLE_ENTRY(ShadowTables, clipmap.levelTable + ClipmapEntryOf(level, at));
        if (entry.tag == ClipmapTagOf(at) && IsReadable(clipmap, entry.page, place))
        {
            page = entry.page & ~PageDirty;
            return true;
        }
    }

    return false;
}

// The share of the key light reaching a camera-relative point, its penumbra as wide as the light's disc makes it (PCSS,
// Fernando 2005), widened through the clipmap's coarser levels rather than more taps: the casters near the point found
// at the coarsest level it may be filtered at, over all the filter reaches there, so every pixel whose filter could see
// a caster finds it and the penumbra fades all the way out, and where no level from there on holds a page, none is
// found rather than the point left lit; their distance along the light times the light's angular diameter the penumbra;
// and the point filtered at the level whose texels a quarter of that wide are, never more than ClipmapFinerLevels finer
// than the pixel's own nor more than ClipmapSoftLevels coarser, so close up a wide penumbra is narrowed to what those
// levels hold. The point is pushed out by a texel of whichever level it is looked up in, and compared a texel of depth
// nearer the light, so a surface facing it, which the push does not move, does not shadow itself. Both the pixel's
// level and the filter's are picked by dither, 0 to 1, where two levels meet (ClipmapLevelFor), so neither steps from
// one to the next.
float KeyLightVirtualShadow(float3 position, float3 normal, float viewDepth, float dither)
{
    GpuClipmap clipmap = ClipmapOf();
    uint level = ClipmapLevelFor(clipmap, viewDepth, dither);
    uint finest = level > ClipmapFinerLevels ? level - ClipmapFinerLevels : 0u;
    uint widest = min(level + ClipmapSoftLevels, clipmap.levels - 1u);
    uint search = widest;
    float3 moved = ClipmapMoved(clipmap, search, position, normal);
    uint page;
    float2 place;
    bool hasSearch = ClipmapDrawnAt(clipmap, search, moved, page, place);
    float depth = ClipmapDepthOf(clipmap, moved);
    float blockers = 0.0;
    float found = 0.0;

    // A 3 x 3 grid two texels apart, which with each texel's own width covers the 5 x 5 tent the filter reads.
    float step = 2.0 / (float)PageContent;
    [unroll] for (uint i = 0u; i < 9u; i++)
    {
        float2 offset = (float2(i % 3u, i / 3u) - 1.0) * step;
        float stored = hasSearch ? PageDepthAt(page, place + offset, clipmap) : -1.0;
        blockers += stored > depth ? stored : 0.0;
        found += stored > depth ? 1.0 : 0.0;
    }

    if (found > 0.0)
    {
        float distance = (blockers / found - depth) * 2.0 * clipmap.depthRange;
        float penumbra = distance * 2.0 * tan(KeyLightAngularRadius);
        float finestTexel = clipmap.finest / (float)ClipmapWindow / (float)PageContent;
        float wanted = log2(max(penumbra / (4.0 * finestTexel), 1.0)) + (dither * 2.0 - 1.0) * ClipmapLevelBlend;
        level = clamp((uint)ceil(max(wanted, 0.0)), finest, widest);
    }

    moved = ClipmapMoved(clipmap, level, position, normal);
    uint drawn = level;
    if (!ClipmapDrawnAt(clipmap, drawn, moved, page, place))
    {
        return 1.0;
    }

    // A coarser level than the one asked for holds the page: the point is pushed out as far as its texels are wide.
    if (drawn != level)
    {
        moved = ClipmapMoved(clipmap, drawn, position, normal);
        ClipmapDrawnAt(clipmap, drawn, moved, page, place);
    }

    float texelDepth = ClipmapLevelOf(drawn).texel / (2.0 * clipmap.depthRange);
    return PageShadow(page, place, ClipmapDepthOf(clipmap, moved) + texelDepth, clipmap);
}

// The share of a point or spot light reaching a receiver toLight from it: its face's page at the mip as fine as the
// pixel wants, or the next coarser drawn, the receiver pushed out along its normal by a texel there, filtered.
float LightVirtualShadow(uint slot, float3 toLight, float3 normal, float viewDepth)
{
    GpuClipmap clipmap = ClipmapOf();
    float distance = length(toLight);
    uint mip = LightMipFor(viewDepth / PixelScale, distance);
    [loop] for (; mip < LightMips; mip++)
    {
        float texel = 2.0 * distance / (float)FaceTexels(mip);
        float3 moved = NormalOffset(-toLight, normal, toLight / max(distance, 1e-6), texel * LightShadowBias);
        uint face = FaceOf(moved);
        float ahead;
        float2 onFace = FacePlaceOf(face, moved, ahead);
        uint side = FacePages(mip);
        uint2 page = min((uint2)(onFace * side), side - 1u);
        uint found = FindLightEntry(ShadowTables, clipmap.lightTable, LightKeyOf(slot, face, mip, page), clipmap.lightEntries);
        if (found == NoView)
        {
            continue;
        }

        ShadowEntry entry = TABLE_ENTRY(ShadowTables, clipmap.lightTable + found);
        if (!IsDrawn(entry.page))
        {
            continue;
        }

        return PageShadow(entry.page, onFace * side - (float2)page, LightNear / ahead, clipmap);
    }

    return 1.0;
}
#endif

#endif
