// One thread per point or spot light: the light is listed in every cell of the level's light grid its range
// reaches, so a voxel asks only the lights near it. A cell more lights reach than it holds keeps the first ones.
// The counts were zeroed by Resolve.comp.hlsl.

#include "Include/Shade.hlsli"
#include "Gi/Common.hlsli"

StructuredBuffer<GpuLight> Lights : READ(0);
RWStructuredBuffer<uint> LightGrid : WRITE(0);

[numthreads(64, 1, 1)]
void main(uint3 threadId : SV_DispatchThreadID)
{
    if (threadId.x >= LightCount)
        return;

    GpuLight light = Lights[threadId.x];
    uint level = GiUpdateLevel;
    float cellSize = GiExtent(level) / (float)GI_LIGHT_CELLS;
    float3 origin = GiOrigin(level);
    float3 center = light.positionRange.xyz;
    float range = light.positionRange.w;
    int cells = (int)GI_LIGHT_CELLS;
    int3 first = (int3)floor((center - range - origin) / cellSize);
    int3 last = (int3)floor((center + range - origin) / cellSize);
    if (any(last < 0) || any(first >= cells))
        return;

    first = clamp(first, 0, cells - 1);
    last = clamp(last, 0, cells - 1);
    [loop] for (int z = first.z; z <= last.z; z++)
    {
        [loop] for (int y = first.y; y <= last.y; y++)
        {
            [loop] for (int x = first.x; x <= last.x; x++)
            {
                uint row = GiLightRow(uint3(x, y, z));
                uint place;
                InterlockedAdd(LightGrid[row], 1u, place);
                if (place < GI_LIGHTS_PER_CELL)
                    LightGrid[row + 1u + place] = threadId.x;
            }
        }
    }
}
