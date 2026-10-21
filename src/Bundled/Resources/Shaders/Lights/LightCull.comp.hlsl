// Finds the world's lights that reach the view, a thread a slot of the table: one whose sphere is in view is counted
// into the z-bin its centre is in, and noted with it.

#include "Include/LightCulling.hlsli"
#include "Include/Pieces.hlsli"

[numthreads(64, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= CountOf(WorldLights, GpuWorldLightBytes))
    {
        return;
    }

    GpuWorldLight light = LOAD(WorldLights, GpuWorldLight, id.x);
    if ((light.flags & WorldLightLive) == 0u)
    {
        return;
    }

    float3 centre = WorldLightPosition(light);
    if (!SphereInView(centre, light.range))
    {
        return;
    }

    uint bin = ZBinOf(max(WorldToView(centre).z, Near));
    uint at = WaveAllocate(LightCounts, CountVisible * 4u, 1u);
    if (at < CountOf(VisibleLights, 8u))
    {
        LightCounts.InterlockedAdd(bin * 4u, 1u);
        VisibleLights.Store2(at * 8u, uint2(id.x, bin));
    }
}
