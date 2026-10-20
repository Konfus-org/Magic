// Lists, for each probe's cell of every level (Lighting/Gi.hlsli's GiCellOf), the point and spot lights that reach it,
// so what the field's rays hit there is lit by them (GiHitRadiance) without looking through every light: up to
// GiCellLights of them, the strongest where they reach the cell first, then GiNoLight. A cell lies where it does in the
// world, so its list stays as its level moves; it is made again each frame, so it follows lights that move. A group is
// 4 x 4 x 4 cells: its threads take the lights that reach the field (Probes/GiLightCull) 64 at a time and keep those
// that reach the group's box, then each sorts those that reach its own cell into its list.

#include "Include/Color.hlsli"
#include "Lighting/Gi.hlsli"
#include "Lighting/Lights.hlsli"

groupshared uint Reaching[64];
groupshared uint ReachingCount;

// How strongly a light reaches a box, as the lighting weighs it at the box's nearest point; 0 where it does not reach.
float Strength(GpuSceneLight light, float3 low, float3 high)
{
    float3 position = light.position;
    return Luminance(light.intensity) * DistanceAttenuation(position - clamp(position, low, high), 1.0 / Pow2(light.range));
}

[numthreads(64, 1, 1)]
void main(uint3 group : SV_GroupID, uint thread : SV_GroupIndex)
{
    uint index = group.x / 64u;
    if (index >= GiHeader().levels)
    {
        return;
    }

    GpuGiLevel level = GiLevel(index);
    float spacing = (float)GiStride * level.voxel;
    int3 first = GiProbeOrigin(level) + 4 * int3(group.x & 3u, (group.x >> 2u) & 3u, (group.x >> 4u) & 3u);
    int3 cell = first + int3(thread & 3u, (thread >> 2u) & 3u, thread >> 4u);
    float3 groupLow = float3(first) * spacing;
    float3 groupHigh = groupLow + 4.0 * spacing;
    float3 low = float3(cell) * spacing;
    float3 high = low + spacing;

    float strengths[GiCellLights];
    uint slots[GiCellLights];
    [unroll] for (uint k = 0u; k < GiCellLights; k++)
    {
        strengths[k] = 0.0;
        slots[k] = GiNoLight;
    }

    uint lights = min(GiFieldLights.Load(0u), CountOf(GiFieldLights, 4u) - 1u);
    [loop] for (uint start = 0u; start < lights; start += 64u)
    {
        if (thread == 0u)
        {
            ReachingCount = 0u;
        }

        GroupMemoryBarrierWithGroupSync();
        if (start + thread < lights)
        {
            uint slot = GiFieldLights.Load((1u + start + thread) * 4u);
            if (Strength(LOAD(SceneLights, GpuSceneLight, slot), groupLow, groupHigh) > 0.0)
            {
                uint at;
                InterlockedAdd(ReachingCount, 1u, at);
                Reaching[at] = slot;
            }
        }

        GroupMemoryBarrierWithGroupSync();
        uint count = ReachingCount;
        [loop] for (uint i = 0u; i < count; i++)
        {
            // Into its place among the strongest, each weaker one moving down a place, the weakest past the last dropped.
            uint candidate = Reaching[i];
            float strength = Strength(LOAD(SceneLights, GpuSceneLight, candidate), low, high);
            [branch] if (strength <= strengths[GiCellLights - 1u])
            {
                continue;
            }

            [unroll] for (uint k = 0u; k < GiCellLights; k++)
            {
                bool isStronger = strength > strengths[k];
                float keptStrength = strengths[k];
                uint kept = slots[k];
                strengths[k] = isStronger ? strength : keptStrength;
                slots[k] = isStronger ? candidate : kept;
                strength = isStronger ? keptStrength : strength;
                candidate = isStronger ? kept : candidate;
            }
        }

        GroupMemoryBarrierWithGroupSync();
    }

    uint listed = GiProbeIndex(cell, index) * GiCellLights;
    [unroll] for (uint k = 0u; k < GiCellLights; k++)
    {
        STORE(GiLights, uint, listed + k, slots[k]);
    }
}
