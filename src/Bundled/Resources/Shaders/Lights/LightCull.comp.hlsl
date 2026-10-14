// Finds the world's lights that reach the view, a thread a slot of the table: one whose sphere is in view is counted
// into the z-bin its centre is in, and noted with it.

#include "Include/LightCulling.hlsli"
#include "Include/Pieces.hlsli"

[numthreads(64, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= CountOf(SceneLights, 80u))
    {
        return;
    }

    GpuSceneLight light = LOAD(SceneLights, GpuSceneLight, id.x);
    if ((light.flags & SceneLightLive) == 0u)
    {
        return;
    }

    float3 centre = SceneLightPosition(light);
    if (!SphereInView(centre, light.range))
    {
        return;
    }

    uint bin = ZBinOf(max(WorldToView(centre).z, Near));
    uint at;
    LightCounts.InterlockedAdd(CountVisible * 4u, 1u, at);
    if (at < CountOf(VisibleLights, 8u))
    {
        LightCounts.InterlockedAdd(bin * 4u, 1u);
        VisibleLights.Store2(at * 8u, uint2(id.x, bin));
    }
}
