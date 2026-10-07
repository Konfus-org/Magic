// The second step that bins the lights: each screen tile cut along its depth into LightSlices clusters
// (Include/Structs.hlsli). One thread per cluster: of the lights LightCull.comp.hlsl found for its tile it
// keeps the ones whose range touches its own slice of the tile's depth, so a pixel is lit by the lights
// near where it is and not by everything along its line of sight. This is what keeps a tile at the horizon,
// which looks along hundreds of metres of ground, from running out of room: each slice of it holds only its
// own stretch, and the slices are cut from the depth the tile actually shows. A cluster more lights reach than its row holds keeps
// the first ones and still counts the rest. A tile that ran out of room itself (it looks along so much
// ground that more lights reach it than its row holds) loses nothing: its clusters look through every light
// instead of the tile's list, which costs those few clusters more and is right all the same.

#include "Include/GBuffer.hlsli"
#include "Lighting/Common.hlsli"

#define GROUP_SIZE 4 // clusters a side per group, across, down and in depth

StructuredBuffer<GpuLight> Lights : READ(0);
StructuredBuffer<uint> Tiles : READ(1);
RWStructuredBuffer<uint> Clusters : WRITE(0);

[numthreads(GROUP_SIZE, GROUP_SIZE, GROUP_SIZE)]
void main(uint3 threadId : SV_DispatchThreadID)
{
    uint2 viewSize = (uint2)ViewSize;
    uint2 tileCount = (viewSize + LightTileSize - 1u) / LightTileSize;
    uint2 tile = threadId.xy;
    uint slice = threadId.z;
    if (any(tile >= tileCount) || slice >= LightSlices)
        return;

    uint tileIndex = tile.y * tileCount.x + tile.x;
    uint tileRow = tileIndex * LightTileWords;
    uint reaching = Tiles[tileRow];
    float tileNear = asfloat(Tiles[tileRow + 1u]);
    float tileFar = asfloat(Tiles[tileRow + 2u]);

    uint clusterRow = (tileIndex * LightSlices + slice) * LightClusterWords;
    uint count = 0u;
    [branch] if (reaching > 0u)
    {
        // The slice's stretch of what was drawn in the tile.
        float nearDepth = LightSliceNearEdge(slice, tileNear, tileFar);
        float farDepth = LightSliceNearEdge(slice + 1u, tileNear, tileFar);

        float3 boxMin;
        float3 boxMax;
        LightTileBox(tile * LightTileSize, nearDepth, farDepth, boxMin, boxMax);

        // The tile's list, or every light when the list could not hold all that reach the tile.
        bool isListWhole = reaching <= LightsPerTile;
        uint listed = isListWhole ? reaching : LightCount;
        [loop] for (uint entry = 0u; entry < listed; entry++)
        {
            uint index = isListWhole ? Tiles[tileRow + LightTileHeader + entry] : entry;
            GpuLight light = Lights[index];
            [branch] if (LightTouchesBox(light, boxMin, boxMax) && (isListWhole || LightScreenFade(light) > 0.0))
            {
                [branch] if (count < LightsPerCluster)
                    Clusters[clusterRow + 1u + count] = index;

                count++;
            }
        }
    }

    Clusters[clusterRow] = count;
}
