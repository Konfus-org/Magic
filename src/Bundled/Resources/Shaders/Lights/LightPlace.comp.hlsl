// Writes the view's lights nearest first, a thread a light that reaches it: each takes the next place of its z-bin's
// run and is written there as the lighting reads a light (GpuLight), its position a float about the camera's, which
// lighting takes the camera's away from again, so it is exact enough however far the world goes. Past the view's room,
// the farthest bins', it is left to the light from everywhere. A light that casts shadows names its slot, where its
// shadow is found.

#include "Include/LightCulling.hlsli"

[numthreads(64, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= min(LightCounts.Load(CountVisible * 4u), CountOf(VisibleLights, 8u)))
    {
        return;
    }

    uint2 visible = VisibleLights.Load2(id.x * 8u);
    uint at;
    LightStarts.InterlockedAdd(visible.y * 4u, 1u, at);
    if (at >= LightCounts.Load(CountHeld * 4u))
    {
        return;
    }

    GpuWorldLight light = LOAD(WorldLights, GpuWorldLight, visible.x);
    float3 centre = WorldLightPosition(light);
    GpuLight placed;
    placed.position = centre;
    placed.invRangeSquared = 1.0 / (light.range * light.range);
    placed.intensity = light.intensity;
    placed.radius = light.radius;
    placed.forward = light.forward;
    placed.range = light.range;
    placed.spotScale = light.spotScale;
    placed.spotOffset = light.spotOffset;
    placed.viewDepth = WorldToView(centre).z;
    placed.isSpot = (light.flags & WorldLightSpot) != 0u ? 1u : 0u;
    placed.shadow = (light.flags & WorldLightShadowless) != 0u ? NoShadowFace : visible.x;
    placed.padding = uint3(0u, 0u, 0u);
    STORE(Lights, GpuLight, at, placed);
}
