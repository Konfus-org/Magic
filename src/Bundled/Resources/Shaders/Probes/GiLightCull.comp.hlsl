// Lists the world's point and spot lights that reach the scene's field, a thread a slot of their table, the count
// first, so the lights of each probe's cell (Probes/GiLights) are found among those alone: the table has a row for every
// entity slot there is, nearly all of them empty. A light reaches the field when its range meets the coarsest level,
// which holds the others.

#include "Lighting/Gi.hlsli"

[numthreads(64, 1, 1)]
void main(uint3 thread : SV_DispatchThreadID)
{
    if (thread.x >= CountOf(SceneLights, GpuSceneLightBytes))
    {
        return;
    }

    GpuSceneLight light = LOAD(SceneLights, GpuSceneLight, thread.x);
    if ((light.flags & SceneLightLive) == 0u)
    {
        return;
    }

    GpuGiLevel coarsest = GiLevel(GiHeader().levels - 1u);
    float3 low = float3(coarsest.origin) * coarsest.voxel;
    float3 high = low + (float)GiResolution * coarsest.voxel;
    float3 position = light.position;
    float3 toField = position - clamp(position, low, high);
    if (dot(toField, toField) >= Pow2(light.range))
    {
        return;
    }

    uint at;
    GiFieldLights.InterlockedAdd(0u, 1u, at);
    if (1u + at < CountOf(GiFieldLights, 4u))
    {
        GiFieldLights.Store((1u + at) * 4u, thread.x);
    }
}
