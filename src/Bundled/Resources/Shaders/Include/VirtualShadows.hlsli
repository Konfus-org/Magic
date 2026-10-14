// Virtual shadow maps, as a renderer drawing clusters keeps them (its Shadows.cs): every shadow is pages of one depth
// pool (ShadowPool), only those the views' pixels read, kept from frame to frame and drawn again where what casts into
// them changed. A page is 128 texels a side, a border of 4 round 120 that hold the shadow, so a filter at its edge
// reads its own border; the pool's last quarter is split into small pages, 32 texels with a border of 2.
//
// The sun's pages are a clipmap: levels about the camera, each twice as wide as the last and 64 pages a side, a level's
// window moving a page at a time, its entries addressed round by the page's place so a page that stays in the window
// keeps its entry. A point or spot light's pages are its six cube faces' at a mip (8, 4, 2 or 1 page a side, or one
// small page), found in a hash table by light, face, mip and page, made anew each frame from what is wanted, carrying
// its pages over from the last frame's table.
//
// The tables are one buffer (ShadowTables) of 16-byte rows: a record for each page of the pool, whole pages' first,
// then the sun's entries, then two tables of lights' entries, this frame's and the last's, which swap each frame; the
// clipmap (SunClipmap) says where each starts. An entry is four words: its pool page plus one (0 for none) with
// PageDirty (it waits to be drawn) and PageSmall (a small page); a sun entry's tag, the page it stands for, or a light
// entry's key, EmptyKey for none; the frame it was last wanted in; and the view drawing it this frame (ShadowViews),
// NoView for none. A page's record is what owns it, a sun entry plus one or a light entry with LightOwner, the frame it
// was last read in, so the pages read longest ago are the first taken for what is wanted now, and the rectangle of it
// waiting to be drawn again: all of it when new, else where what moved since it was drawn reaches, as only that can
// have changed, so a small caster moving in a big page draws little. The views read a page only when it is not dirty,
// else the next coarser, so nothing is ever unshadowed for waiting: a sun level past the last, or a light with none, is
// lit.

#ifndef MAGIC_VIRTUAL_SHADOWS_HLSLI
#define MAGIC_VIRTUAL_SHADOWS_HLSLI

#include "Include/Frame.hlsli"
#include "Include/Noise.hlsli"
#include "Include/Structs.hlsli"

static const uint PageTexels = 128u;
static const uint PageBorder = 4u;
static const uint PageContent = 120u;
static const uint SmallTexels = 32u;
static const uint SmallBorder = 2u;
static const uint SmallContent = 28u;
static const uint SunWindow = 64u;
static const uint SunEntriesPerLevel = SunWindow * SunWindow;
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

// Texels of a page a receiver is pushed out along its normal, so it does not shadow itself: the sun's, and a point or
// spot light's, whose faces' texels meet a receiver at steeper angles.
static const float ShadowBias = 1.5;
static const float LightShadowBias = 3.0;

// Pixels a texel of a point or spot light's face covers at most: two, as its shadow is soft, which wants a quarter of
// the pages one would, so many lights near the camera leave the pool room for the sun.
static const float LightTexelPixels = 2.0;

// A view's ShadowArgs, by byte: the indirect draws of the pages' clear and of the shadows' pieces, five words each;
// whether the culls placed more jobs or pieces than there was room for, which leaves the pages drawn with them short of
// casters, so the next frame draws them all again; how many pages a frame may draw, halved after a frame that ran out
// of room and doubled back after one that did not, up to ShadowViews; and the indirect dispatches of the shadows'
// instance and cluster culls, three words each on rows of twelve bytes. Its ShadowCounts, apart, as no pass writes what
// it is dispatched by: how many pages are drawn, how many groups they are in, and the jobs and pieces the culls placed.
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

// The sun's clipmap this frame (the renderer's GpuSunClipmap): the sun's frame, how many levels, the frame's number,
// whether every page starts again, the main camera's depth along the sun, how deep a page reaches either side, the
// finest level's width, the changed lights' bits' words, the pool's layout, how many spheres moved (Moved), where the
// main camera is, which the clipmap is placed about and the moved spheres are relative to, and where the tables start
// in ShadowTables and how many entries a light table has. Its levels follow it, from SunLevelsAt.
struct GpuSunClipmap
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
    float3 cameraHigh;
    uint sunTable;
    float3 cameraLow;
    uint lightTable;
    uint lightTableLast;
    uint lightEntries;
    uint2 padding;
};

static const uint SunLevelsAt = 128u;

// One level of the clipmap: its window's first page, counted from the sun's origin, metres a page and a texel, and
// where the main camera is across the sun from the window's corner.
struct GpuSunLevel
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
// group, which culling finds its casters by, and the rectangle of it drawn, which is all that is cleared and drawn.
struct GpuShadowView
{
    float4 row0;
    float4 row1;
    float4 row2;
    float4 row3;
    uint page;
    uint group;
    uint rect;
    uint padding;
};

// What culling finds casters by: one sun page drawn, or the views of one light: the page's level, or the light's slot
// with LightOwner; the faces and mips the light's views are at as bits, face * LightMips + mip; the level's metres a
// texel; the sun page's view, NoView for a light; and where the light is, relative to the camera, and how far it
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
float3 MainRelative(GpuSunClipmap sun, float3 position)
{
    return position + (CameraPosition - sun.cameraHigh) + (CameraLow - sun.cameraLow);
}

#if defined(HAS_SunClipmap)
GpuSunClipmap SunClipmapOf()
{
    return LOAD(SunClipmap, GpuSunClipmap, 0u);
}

GpuSunLevel SunLevelOf(uint level)
{
    return SunClipmap.Load<GpuSunLevel>(SunLevelsAt + level * 32u);
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
uint RecordOf(GpuSunClipmap sun, uint page)
{
    uint index = (page & PageNumber) - 1u;
    return (page & PageSmall) != 0u ? sun.poolAcross * sun.poolAcross + index : index;
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

#if defined(HAS_ShadowArgs) && defined(HAS_ShadowViews)
// The rectangle a page last frame's view drew is drawn again with, as that frame ran out of room for casters and may
// have left some out; none when it did not, or no view drew the page, or it drew no other page, so there is nothing
// fewer to draw at once and what was drawn is kept.
uint ShortRect(uint view)
{
    if (view >= CountOf(ShadowViews, 80u) || ShadowArgs.Load(ShadowArgsOverflowed) != 1u || ShadowArgs.Load(ShadowArgsViewRoom) <= 1u)
    {
        return 0u;
    }

    return LOAD(ShadowViews, GpuShadowView, view).rect;
}
#endif

// The pool's texel a page's corner is at.
uint2 PageCorner(uint page, GpuSunClipmap sun)
{
    uint index = (page & PageNumber) - 1u;
    if ((page & PageSmall) != 0u)
    {
        uint whole = sun.wholePages + index / 16u;
        uint part = index % 16u;
        return uint2(whole % sun.poolAcross, whole / sun.poolAcross) * PageTexels + uint2(part % 4u, part / 4u) * SmallTexels;
    }

    return uint2(index % sun.poolAcross, index / sun.poolAcross) * PageTexels;
}

// The pool's texel at a place in a page's shadow, 0 to 1 across what it holds.
float2 PageTexel(uint page, float2 place, GpuSunClipmap sun)
{
    bool isSmall = (page & PageSmall) != 0u;
    return (float2)PageCorner(page, sun) + (isSmall ? SmallBorder : PageBorder) + saturate(place) * (isSmall ? SmallContent : PageContent);
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

// The view depth at which pixels want a sun level: where a pixel covers as much as its texels do.
float SunReachOf(GpuSunClipmap sun, uint level)
{
    return sun.finest / (float)SunWindow / (float)PageContent * exp2((float)level) * LodScale;
}

// The sun level whose texels are as fine as a pixel at a view depth: a pixel there covers viewDepth / LodScale metres.
uint SunLevelFor(GpuSunClipmap sun, float viewDepth)
{
    float finestTexel = sun.finest / (float)SunWindow / (float)PageContent;
    float wanted = max(viewDepth / LodScale / finestTexel, 1.0);
    return min((uint)ceil(log2(wanted)), sun.levels - 1u);
}

// Which page of a sun level a camera-relative point is on, counted from the sun's origin, and where on it, 0 to 1;
// false when the point is outside the level's window.
bool SunPageOf(GpuSunClipmap sun, GpuSunLevel level, float3 position, out int2 page, out float2 place)
{
    float3 main = MainRelative(sun, position);
    float2 across = float2(dot(main, sun.right), dot(main, sun.up)) + level.camera;
    float2 pages = across / level.pageSize;
    page = int2(0, 0);
    place = float2(0.0, 0.0);
    if (any(pages < 0.0) || any(pages >= (float)SunWindow))
    {
        return false;
    }

    int2 local = (int2)floor(pages);
    page = level.origin + local;
    place = pages - (float2)local;
    return true;
}

// A sun page's entry, counted from the sun's first: by level, its place in the window addressed round.
uint SunEntryOf(uint level, int2 page)
{
    uint2 wrapped = (uint2)(page & int(SunWindow - 1u));
    return level * SunEntriesPerLevel + wrapped.y * SunWindow + wrapped.x;
}

uint SunTagOf(int2 page)
{
    return ((uint)page.x & 0xFFFFu) | (((uint)page.y & 0xFFFFu) << 16);
}

// The page a sun entry stands for now, its level's window having moved: its place, counted from the sun's origin.
int2 SunPageAt(GpuSunLevel level, uint entry)
{
    uint local = entry % SunEntriesPerLevel;
    int2 wrapped = int2(local % SunWindow, local / SunWindow);
    int2 offset = (wrapped - (level.origin & int(SunWindow - 1u)) + int(SunWindow)) & int(SunWindow - 1u);
    return level.origin + offset;
}

// A camera-relative point's depth from the sun, reverse-Z: nearer the sun is more.
float SunDepthOf(GpuSunClipmap sun, float3 position)
{
    float along = dot(MainRelative(sun, position), sun.direction) + sun.cameraDepth;
    return 0.5 - along / (2.0 * sun.depthRange);
}

// A cube face's axes: the way it looks, and right and up across it, right = cross(up, forward).
void FaceAxes(uint face, out float3 forward, out float3 right, out float3 up)
{
    float3 axes[6] = { float3(1.0, 0.0, 0.0), float3(-1.0, 0.0, 0.0), float3(0.0, 1.0, 0.0), float3(0.0, -1.0, 0.0), float3(0.0, 0.0, 1.0), float3(0.0, 0.0, -1.0) };
    float3 ups[6] = { float3(0.0, 1.0, 0.0), float3(0.0, 1.0, 0.0), float3(0.0, 0.0, -1.0), float3(0.0, 0.0, 1.0), float3(0.0, 1.0, 0.0), float3(0.0, 1.0, 0.0) };
    forward = axes[face];
    up = ups[face];
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
// linear probing from its key's hash; NoView when it is not there.
uint FindLightEntry(ByteAddressBuffer tables, uint first, uint key, uint entries)
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
uint FindLightEntry(RWByteAddressBuffer tables, uint first, uint key, uint entries)
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

#if defined(HAS_ShadowPool) && defined(HAS_ShadowTables) && defined(HAS_SunClipmap)
#include "Lighting/Shadows.hlsli"

float PoolSize()
{
    float width;
    float height;
    ShadowPool.GetDimensions(width, height);
    return width;
}

// The depth a page holds at a place on it, unfiltered.
float PageDepthAt(uint page, float2 place, GpuSunClipmap sun)
{
    return ShadowPool.Load(int3(PageTexel(page, place, sun), 0));
}

// The share of light reaching a receiver through a page: a 5 x 5 tent of comparisons (Lighting/Shadows.hlsli), which
// the page's border keeps inside it.
float PageShadow(uint page, float2 place, float depth, GpuSunClipmap sun)
{
    float size = PoolSize();
    return ShadowPcf(ShadowPool, ShadowPoolSampler, PageTexel(page, place, sun) / size, depth, float2(size, size));
}

// The sun level at or past level that holds a drawn page at a point: its entry's page, and where on it; false past
// the last level.
bool SunDrawnAt(GpuSunClipmap sun, inout uint level, float3 position, out uint page, out float2 place)
{
    page = 0u;
    place = float2(0.0, 0.0);
    [loop] for (; level < sun.levels; level++)
    {
        int2 at;
        if (!SunPageOf(sun, SunLevelOf(level), position, at, place))
        {
            continue;
        }

        ShadowEntry entry = TABLE_ENTRY(ShadowTables, sun.sunTable + SunEntryOf(level, at));
        if (entry.tag == SunTagOf(at) && IsDrawn(entry.page))
        {
            page = entry.page;
            return true;
        }
    }

    return false;
}

// A camera-relative point pushed out along its normal by a texel of a sun level, as it looks itself up there.
float3 SunMoved(GpuSunClipmap sun, uint level, float3 position, float3 normal)
{
    return NormalOffset(position, normal, -sun.direction, SunLevelOf(min(level, sun.levels - 1u)).texel * ShadowBias);
}

// The share of the sun reaching a camera-relative point, its penumbra as wide as the sun's disc makes it (PCSS,
// Fernando 2005), widened through the clipmap's coarser levels rather than more taps: the casters near the point found
// two levels coarser than the pixel wants, their distance along the sun times the sun's angular diameter the penumbra,
// and the point filtered at the level whose texels a third of that wide are, never finer than the pixel's own. The
// point is pushed out by a texel of whichever level it is looked up in.
float SunVirtualShadow(float3 position, float3 normal, float viewDepth)
{
    GpuSunClipmap sun = SunClipmapOf();
    uint level = SunLevelFor(sun, viewDepth);
    uint finest = level;
    uint search = min(level + 2u, sun.levels - 1u);
    float3 moved = SunMoved(sun, search, position, normal);
    uint page;
    float2 place;
    if (!SunDrawnAt(sun, search, moved, page, place))
    {
        return 1.0;
    }

    float depth = SunDepthOf(sun, moved);
    float blockers = 0.0;
    float found = 0.0;
    float step = 1.5 / (float)PageContent;
    [unroll] for (uint i = 0u; i < 5u; i++)
    {
        float2 offset = i == 0u ? float2(0.0, 0.0) : float2(i % 2u == 0u ? 1.0 : -1.0, i < 3u ? 1.0 : -1.0) * step;
        float stored = PageDepthAt(page, place + offset, sun);
        blockers += stored > depth ? stored : 0.0;
        found += stored > depth ? 1.0 : 0.0;
    }

    if (found > 0.0)
    {
        float distance = (blockers / found - depth) * 2.0 * sun.depthRange;
        float penumbra = distance * 2.0 * tan(SunAngularRadius);
        float finestTexel = sun.finest / (float)SunWindow / (float)PageContent;
        level = max(finest, min((uint)ceil(log2(max(penumbra / (3.0 * finestTexel), 1.0))), sun.levels - 1u));
    }

    level = max(level, finest);
    moved = SunMoved(sun, level, position, normal);
    uint drawn = level;
    if (!SunDrawnAt(sun, drawn, moved, page, place))
    {
        return 1.0;
    }

    // A coarser level than the one asked for holds the page: the point is pushed out as far as its texels are wide.
    if (drawn != level)
    {
        moved = SunMoved(sun, drawn, position, normal);
        SunDrawnAt(sun, drawn, moved, page, place);
    }

    return PageShadow(page, place, SunDepthOf(sun, moved), sun);
}

// The share of a point or spot light reaching a receiver toLight from it: its face's page at the mip as fine as the
// pixel wants, or the next coarser drawn, the receiver pushed out along its normal by a texel there, filtered.
float LightVirtualShadow(uint slot, float3 toLight, float3 normal, float viewDepth)
{
    GpuSunClipmap sun = SunClipmapOf();
    float distance = length(toLight);
    uint mip = LightMipFor(viewDepth / LodScale, distance);
    [loop] for (; mip < LightMips; mip++)
    {
        float texel = 2.0 * distance / (float)FaceTexels(mip);
        float3 moved = NormalOffset(-toLight, normal, toLight / max(distance, 1e-6), texel * LightShadowBias);
        uint face = FaceOf(moved);
        float ahead;
        float2 onFace = FacePlaceOf(face, moved, ahead);
        uint side = FacePages(mip);
        uint2 page = min((uint2)(onFace * side), side - 1u);
        uint found = FindLightEntry(ShadowTables, sun.lightTable, LightKeyOf(slot, face, mip, page), sun.lightEntries);
        if (found == NoView)
        {
            continue;
        }

        ShadowEntry entry = TABLE_ENTRY(ShadowTables, sun.lightTable + found);
        if (!IsDrawn(entry.page))
        {
            continue;
        }

        return PageShadow(entry.page, onFace * side - (float2)page, LightNear / ahead, sun);
    }

    return 1.0;
}
#endif

#endif
