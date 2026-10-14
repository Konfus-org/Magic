// The view's light tiles, turned round: a thread per light the view holds sets its bit in the mask of every tile its
// sphere covers on screen (Include/LightCulling.hlsli's TilesOf), so the cost follows how much of the screen the lights
// cover, not how many there are times how many tiles; the z-bins tell depths apart. The masks start empty
// (LightTilesClear).

#include "Include/LightCulling.hlsli"

[numthreads(64, 1, 1)]
void main(uint3 thread : SV_DispatchThreadID)
{
    if (thread.x >= LightCounts.Load(CountHeld * 4u))
    {
        return;
    }

    GpuLight light = LOAD(Lights, GpuLight, thread.x);
    uint4 tiles = TilesOf(light.position, light.range);
    uint word = thread.x >> 5u;
    uint bit = 1u << (thread.x & 31u);
    [loop] for (uint y = tiles.y; y <= tiles.w; y++)
    {
        [loop] for (uint x = tiles.x; x <= tiles.z; x++)
        {
            LightTiles.InterlockedOr(((y * TileCountX + x) * TileWords + word) * 4u, bit);
        }
    }
}
