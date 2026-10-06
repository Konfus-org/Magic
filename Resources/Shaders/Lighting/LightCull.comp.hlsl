// The first of the two steps that bin the lights (Include/Structs.hlsli): into the view's screen tiles. One
// thread per tile: it reads the depth range of what was drawn in its pixels, builds the view-space box that
// encloses that part of the view volume, and writes the lights whose range touches the box into the tile's
// row, after their count and the two depths. LightCluster.comp.hlsl then cuts each tile along its depth, so
// this step only has to be roughly right: a far tile, which covers a long stretch of ground, may hold
// hundreds of lights here. A tile nothing was drawn in, and every tile of a frame without lights, gets
// none. A light too small on screen to see is left out of every tile (Lighting/Common.hlsli). A tile more
// lights reach than its row holds keeps the first ones and still counts the rest: its clusters then look
// through every light instead of this list. The CPU uploads the lights as they are; nothing about which light reaches where is
// decided there.

#include "Include/GBuffer.hlsli"
#include "Lighting/Common.hlsli"

#define GROUP_SIZE 8 // tiles a side per group

// With TRANSPARENT_TILES 1 (Resources/Passes/Core/TransparentLightCull.pass) a tile reaches from the near plane to the
// farthest depth drawn in it, or TransparentReach where nothing was: the lights of anything in front of the opaque
// scene, which is where transparent surfaces are drawn (Templates/Forward.frag.hlsl).
#ifndef TRANSPARENT_TILES
#define TRANSPARENT_TILES 0
#endif
static const float TransparentReach = 10000.0;

Texture2D<float> Depth : READ(0);
SamplerState DepthSampler : SAMPLER(0);
StructuredBuffer<GpuLight> Lights : READ(1);
RWStructuredBuffer<uint> Tiles : WRITE(0);

[numthreads(GROUP_SIZE, GROUP_SIZE, 1)]
void main(uint3 threadId : SV_DispatchThreadID)
{
    uint2 viewSize = (uint2)ViewSize;
    uint2 tileCount = (viewSize + LightTileSize - 1u) / LightTileSize;
    uint2 tile = threadId.xy;
    if (any(tile >= tileCount))
        return;

    uint depthWidth;
    uint depthHeight;
    Depth.GetDimensions(depthWidth, depthHeight);
    float2 depthTexelSize = 1.0 / float2(depthWidth, depthHeight);

    // The nearest and the farthest depth drawn in the tile. Reverse-Z: nearer is greater, 0 is nothing.
    uint2 firstPixel = tile * LightTileSize;
    float nearest = 0.0;
    float farthest = 1.0;
    [branch] if (LightCount > 0u)
    {
        [loop] for (uint y = 0u; y < LightTileSize; y++)
        {
            [loop] for (uint x = 0u; x < LightTileSize; x++)
            {
                uint2 pixel = min(firstPixel + uint2(x, y), viewSize - 1u);
                float depth = Depth.SampleLevel(DepthSampler, (ViewOrigin + float2(pixel) + 0.5) * depthTexelSize, 0.0);
                nearest = max(nearest, depth);
                farthest = min(farthest, depth > 0.0 ? depth : 1.0);
            }
        }
    }

    uint row = (tile.y * tileCount.x + tile.x) * LightTileWords;
    uint count = 0u;
    float nearDepth = 0.0;
    float farDepth = 0.0;
    bool drawn = nearest > 0.0;
#if TRANSPARENT_TILES
    [branch] if (LightCount > 0u)
#else
    [branch] if (drawn)
#endif
    {
#if TRANSPARENT_TILES
        nearDepth = Near;
        farDepth = drawn ? ViewDepth(farthest) : min(Far, TransparentReach);
#else
        nearDepth = ViewDepth(nearest);
        farDepth = ViewDepth(farthest);
#endif

        float3 boxMin;
        float3 boxMax;
        LightTileBox(firstPixel, nearDepth, farDepth, boxMin, boxMax);

        [loop] for (uint index = 0u; index < LightCount; index++)
        {
            GpuLight light = Lights[index];
            [branch] if (LightTouchesBox(light, boxMin, boxMax) && LightScreenFade(light) > 0.0)
            {
                // Past the row's end only the count goes on: it says how many were left out.
                [branch] if (count < LightsPerTile)
                    Tiles[row + LightTileHeader + count] = index;

                count++;
            }
        }
    }

    Tiles[row] = count;
    Tiles[row + 1u] = asuint(nearDepth);
    Tiles[row + 2u] = asuint(farDepth);
}
