// What a renderer drawing clusters culls its lights with (Shaders/Lights/): the world's lights are a table on the GPU
// (SceneLights), and each view finds those that reach it, sorts them by the z-bin their centre is in, a count and a
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

#endif
