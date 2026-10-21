// What a renderer drawing clusters culls its lights with (Shaders/Lights/): the world's lights are a table on the GPU
// (WorldLights), and each view finds those that reach it, sorts them by the z-bin their centre is in, a count and a
// prefix per bin, which Drobot's z-bins only need (Drobot 2017, "Improved Culling for Tiled and Clustered Rendering"),
// writes them nearest first as the lighting reads them (Lights), then gives each z-bin the first and last that can reach
// its depths, and each tile a bit for each that reaches it, each light setting the bits of the tiles it covers.
// LightCounts holds a count per z-bin, then how many lights reach the view, then how many of them the view holds,
// which is fewer only when they outnumber it: the farthest are left to the light from everywhere.

#ifndef MAGIC_LIGHT_CULLING_HLSLI
#define MAGIC_LIGHT_CULLING_HLSLI

#include "Include/Frame.hlsli"
#include "Include/Structs.hlsli"

static const uint ZBins = 1024u;
static const uint CountVisible = ZBins;
static const uint CountHeld = ZBins + 1u;

// The rectangle of tiles a light's sphere covers on the view as it is drawn, a sub-pixel off (Jitter), conservatively:
// all of them where it reaches the camera.
uint4 TilesOf(float3 centre, float radius)
{
    uint2 tiles = ((uint2)ViewSize + LightTileSize - 1u) / LightTileSize;
    float3 seen = WorldToView(centre);
    if (seen.z - radius <= Near)
    {
        return uint4(0u, 0u, tiles - 1u);
    }

    float2 ndc = seen.xy / seen.z * ProjScale + Jitter;
    float2 extent = radius / (seen.z - radius) * ProjScale;
    float2 low = float2(ndc.x - extent.x, -(ndc.y + extent.y)) * 0.5 + 0.5;
    float2 high = float2(ndc.x + extent.x, -(ndc.y - extent.y)) * 0.5 + 0.5;
    uint2 first = (uint2)(saturate(low) * ViewSize) / LightTileSize;
    uint2 last = min((uint2)(saturate(high) * ViewSize) / LightTileSize, tiles - 1u);
    return uint4(first, last);
}

// The bits of a mask word that are lights first to last.
uint RangeMask(uint word, uint first, uint last)
{
    uint start = word * 32u;
    bool isOutside = first > last || last < start || first > start + 31u;
    uint low = max(first, start) - start;
    uint high = min(last, start + 31u) - start;
    return isOutside ? 0u : (0xFFFFFFFFu >> (31u - high)) & (0xFFFFFFFFu << low);
}

#if defined(HAS_LightBins) && defined(HAS_LightTiles)
// The lights that reach a pixel, as whatever lights it walks them: the first and last of the view's lights that can
// reach the depths of its z-bin (LightBins), and the words of its tile's mask (LightTiles), of which it visits the bits
// between the two. The words visited are merged across the wave, as each word's bits are (LightWalkMask), so the walk
// is the same for every lane; a light a lane did not need gives it nothing, its reach ending where it does.
struct LightWalk
{
    uint first;
    uint last;
    uint tileWords;
    uint firstWord;
    uint lastWord;
};

LightWalk LightWalkOf(float2 pixel, float viewDepth)
{
    LightWalk walk;
    uint2 range = LightBins.Load2(ZBinOf(viewDepth) * 8u);
    walk.first = range.x;
    walk.last = range.y;
    uint2 tile = (uint2)pixel / LightTileSize;
    walk.tileWords = (tile.y * TileCountX + tile.x) * TileWords;
    walk.firstWord = WaveActiveMin(range.x <= range.y ? range.x >> 5u : 0xFFFFFFFFu);
    walk.lastWord = WaveActiveMax(range.x <= range.y ? range.y >> 5u : 0u);
    return walk;
}

// The lights of a word of the walk that any lane's pixel needs, a bit each: the same for every lane, so the light
// each bit names is read once for the wave (WaveReadLaneFirst).
uint LightWalkMask(LightWalk walk, uint word)
{
    return WaveActiveBitOr(LightTiles.Load((walk.tileWords + word) * 4u) & RangeMask(word, walk.first, walk.last));
}
#endif

#endif
