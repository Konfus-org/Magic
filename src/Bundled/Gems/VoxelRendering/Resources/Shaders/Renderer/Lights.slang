// Culls the world's point and spot lights for a view (Drobot 2017, "Improved Culling for Tiled and Clustered
// Rendering"): every light in view placed in a list sorted by the nearest depth it reaches, each of the view's depth
// slices given the first and last place in the list of the lights that reach it, and each tile of 16 by 16 pixels a
// bit for every place in the list whose light reaches it. A pixel walks its slice's run of the list, masked by its
// tile's bits (ShadeLocalLights). The passes, in order:
//
//   BinLights        a light slot a thread: in view, placed among the visible with its depth; the threads past the
//                    lights empty the slices and tiles; its last group sorts the visible by depth, and writes the
//                    groups the next pass runs
//   BuildLightTiles  a group a visible light: the runs of the slices it reaches widened to its place, and its bit set
//                    in every tile it reaches
//
// Slices are spaced evenly in the logarithm of depth from the view's near plane to its far one, so a slice is as deep
// as it is far, near and far alike.

module "Renderer/Lights";

import Core.Compute;
__exported import Renderer.Pieces;

// Pixels a tile is across and down.
public static const uint LightTile = 16u;

// Depth slices a view is cut into.
public static const uint LightBins = 1024u;

// Visible lights a view holds at most: what one group sorts.
public static const uint MaxVisibleLights = 4096u;

// The view's lights, the renderer's LightList member for member.
public struct LightList
{
    ArrayRef<uint> lights;   // the visible lights' slots, nearest first; as many rows as the view holds lights
    ArrayRef<uint> bins;     // each depth slice's first and last place in lights, two rows each, first > last if none
    ArrayRef<uint> tiles;    // each tile's bits, a word for each 32 places in lights
}

// The passes' constants, the renderer's LightCulling.
struct LightCullConstants
{
    LightList list;
    ArrayRef<uint> keys;     // each visible light's depth, and its place among visible
    ArrayRef<uint> visible;  // the visible lights' slots, as they were found
    ArrayRef<uint> counts;   // the counts below
    ArrayRef<uint3> args;    // the groups BuildLightTiles runs
}

// Counts, a uint each.
static const uint CountFound = 0u;      // visible lights found this frame
static const uint CountGroupsDone = 1u; // the groups of BinLights that finished (Core/Compute's IsLastGroup)
static const uint CountVisible = 2u;    // visible lights placed in the list

groupshared uint SortKeys[MaxVisibleLights];

// Where a view depth falls among the slices, 0 at the near plane to 1 at the far one.
float GetLightBinFraction(float viewDepth)
{
    float depthRange = log2(max(View.far / View.near, 2.0));
    return saturate(log2(max(viewDepth, View.near) / View.near) / depthRange);
}

// The depth slice a view depth falls in.
uint GetLightBin(float viewDepth)
{
    return min((uint)(GetLightBinFraction(viewDepth) * (float)LightBins), LightBins - 1u);
}

// The view's tiles across and down.
uint2 GetLightTiles()
{
    return ((uint2)View.viewSize + LightTile - 1u) / LightTile;
}

// The words each tile has: one for each 32 places in the list.
uint GetTileWords(LightList list)
{
    return (list.lights.count + 31u) / 32u;
}

// The tiles a sphere seen at seen covers: their first and last across and down. Each side of the box about it is
// projected from both its nearest depth and its farthest and the wider taken, which holds whichever side of the view's
// middle it is on; one reaching in front of the near plane covers the whole view.
uint4 GetLightTileRect(float3 seen, float radius, uint2 tiles)
{
    float nearest = seen.z - radius;
    if (nearest <= View.near)
    {
        return uint4(0u, 0u, tiles - 1u);
    }

    float farthest = seen.z + radius;
    float2 low = (seen.xy - radius) * View.projScale;
    float2 high = (seen.xy + radius) * View.projScale;
    float2 lowNdc = min(low / nearest, low / farthest);
    float2 highNdc = max(high / nearest, high / farthest);

    // Down the view is up the screen, so the bottom of the box is the last row.
    float2 first = float2(lowNdc.x * 0.5 + 0.5, 0.5 - highNdc.y * 0.5) * View.viewSize / (float)LightTile;
    float2 last = float2(highNdc.x * 0.5 + 0.5, 0.5 - lowNdc.y * 0.5) * View.viewSize / (float)LightTile;
    int2 most = (int2)tiles - 1;
    return uint4((uint2)clamp((int2)floor(first), 0, most), (uint2)clamp((int2)floor(last), 0, most));
}

// Empties one slice's run or one word of the tiles, by the thread's place among the pass's.
void ClearLightList(LightList list, uint index)
{
    if (index < LightBins)
    {
        list.bins[index * 2u] = 0xFFFFFFFFu;
        list.bins[index * 2u + 1u] = 0u;
    }

    if (index < list.tiles.count)
    {
        list.tiles[index] = 0u;
    }
}

// One light slot: a light whose sphere of reach is in view is placed among the visible, keyed by the nearest depth it
// reaches and its place.
void BinLight(LightCullConstants c, uint slot)
{
    if (slot >= World.localLights.count)
    {
        return;
    }

    LocalLight light = World.localLights[slot];
    if (light.range <= 0.0 || all(light.intensity <= 0.0))
    {
        return;
    }

    float3 centre = light.position - View.cameraPosition;
    if (!IsSphereInView(centre, light.range))
    {
        return;
    }

    uint at = WaveAllocate(c.counts, CountFound, 1u);
    if (at >= min(c.visible.count, MaxVisibleLights))
    {
        return;
    }

    float nearest = View.ToView(centre).z - light.range;
    uint depth = min((uint)(GetLightBinFraction(nearest) * 1048575.0), 1048575u);
    c.keys.StoreAtomic(at, (depth << 12) | at);
    c.visible.StoreAtomic(at, slot);
}

// Sorts the visible lights by their keys, nearest first, in one group (Batcher 1968's bitonic sort), and writes them
// in order into the list; then the groups the tiles' pass runs, a light a group, and the count found emptied for the
// next frame. Every lane of the group calls it.
void SortVisibleLights(LightCullConstants c, uint thread)
{
    uint count = min(c.counts.LoadAtomic(CountFound), min(c.visible.count, MaxVisibleLights));
    uint size = 1u;
    while (size < count)
    {
        size <<= 1;
    }

    for (uint i = thread; i < size; i += 64u)
    {
        SortKeys[i] = i < count ? c.keys.LoadAtomic(i) : 0xFFFFFFFFu;
    }

    GroupMemoryBarrierWithGroupSync();
    for (uint k = 2u; k <= size; k <<= 1)
    {
        for (uint j = k >> 1; j > 0u; j >>= 1)
        {
            for (uint i = thread; i < size; i += 64u)
            {
                uint partner = i ^ j;
                if (partner > i)
                {
                    uint a = SortKeys[i];
                    uint b = SortKeys[partner];
                    bool isAscending = (i & k) == 0u;
                    if ((a > b) == isAscending)
                    {
                        SortKeys[i] = b;
                        SortKeys[partner] = a;
                    }
                }
            }

            GroupMemoryBarrierWithGroupSync();
        }
    }

    for (uint i = thread; i < count; i += 64u)
    {
        c.list.lights[i] = c.visible.LoadAtomic(SortKeys[i] & 0xFFFu);
    }

    if (thread == 0u)
    {
        c.counts[CountVisible] = count;
        c.args[0u] = GetDispatchGroups(count, 1u);
        c.counts.StoreAtomic(CountFound, 0u);
    }
}

[shader("compute")]
[numthreads(64, 1, 1)]
void BinLights(
    uint3 group : SV_GroupID,
    uint thread : SV_GroupIndex,
    uniform PassHeader pass,
    uniform LightCullConstants c)
{
    BeginPass(pass);
    uint index = GetLinearGroup(group) * 64u + thread;
    ClearLightList(c.list, index);
    BinLight(c, index);
    if (IsLastGroup(c.counts, CountGroupsDone, thread))
    {
        SortVisibleLights(c, thread);
    }
}

// One visible light, by its place in the list: the slices from the nearest depth it reaches to the farthest take its
// place into their runs, and every tile it covers gets its bit, the group's lanes sharing the slices and tiles out.
[shader("compute")]
[numthreads(64, 1, 1)]
void BuildLightTiles(
    uint3 group : SV_GroupID,
    uint thread : SV_GroupIndex,
    uniform PassHeader pass,
    uniform LightCullConstants c)
{
    BeginPass(pass);
    uint place = GetLinearGroup(group);
    if (place >= c.counts[CountVisible])
    {
        return;
    }

    LocalLight light = World.localLights[c.list.lights[place]];
    float3 seen = View.ToView(light.position - View.cameraPosition);
    uint firstBin = GetLightBin(seen.z - light.range);
    uint lastBin = GetLightBin(seen.z + light.range);
    for (uint bin = firstBin + thread; bin <= lastBin; bin += 64u)
    {
        c.list.bins.InterlockedMin(bin * 2u, place);
        c.list.bins.InterlockedMax(bin * 2u + 1u, place);
    }

    uint2 tiles = GetLightTiles();
    uint4 rect = GetLightTileRect(seen, light.range, tiles);
    uint width = rect.z - rect.x + 1u;
    uint covered = width * (rect.w - rect.y + 1u);
    uint words = GetTileWords(c.list);
    uint word = place >> 5;
    uint bit = 1u << (place & 31u);
    for (uint i = thread; i < covered; i += 64u)
    {
        uint2 tile = uint2(rect.x + i % width, rect.y + i / width);
        c.list.tiles.InterlockedOr((tile.y * tiles.x + tile.x) * words + word, bit);
    }
}

// The luminance a surface at a pixel sends toward the camera from the point and spot lights that reach it, each
// shadowed as shadowing says: the lights of its depth slice's run whose bits its tile has. The wave walks the runs and
// tiles of all its pixels together, so each light is loaded once for all of them; a light one pixel's tile lacks gives
// it nothing, as it does not reach it.
public float3 ShadeLocalLights<S : IShadowing>(
    LightList list,
    uint2 pixel,
    float viewDepth,
    Shading shading,
    float3 position,
    S shadowing)
{
    uint bin = GetLightBin(viewDepth);
    uint first = WaveActiveMin(list.bins[bin * 2u]);
    uint last = WaveActiveMax(list.bins[bin * 2u + 1u]);
    float3 luminance = float3(0.0, 0.0, 0.0);
    if (first > last)
    {
        return luminance;
    }

    uint2 tiles = GetLightTiles();
    uint2 tile = min(pixel / LightTile, tiles - 1u);
    uint words = GetTileWords(list);
    uint firstWord = (tile.y * tiles.x + tile.x) * words;
    [loop] for (uint word = first >> 5; word <= last >> 5; word++)
    {
        // The run's places in this word alone.
        uint low = word == first >> 5 ? first & 31u : 0u;
        uint high = word == last >> 5 ? last & 31u : 31u;
        uint run = (0xFFFFFFFFu << low) & (0xFFFFFFFFu >> (31u - high));
        uint mask = WaveActiveBitOr(list.tiles[firstWord + word]) & run;
        [loop] while (mask != 0u)
        {
            uint place = word * 32u + firstbitlow(mask);
            mask &= mask - 1u;
            LocalLight light = World.localLights[list.lights[place]];
            light.position -= View.cameraPosition;
            luminance += ShadeLight(shading, position, light, shadowing);
        }
    }

    return luminance;
}
