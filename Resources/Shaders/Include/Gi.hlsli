// How a pixel reads the GI volumes: the sky it sees, the interior tint where it does not, and the bounced light
// gathered along a normal. The caller passes the fade (voxels from a level's edge over which it blends into the
// next) and declares these before including it:
//   Texture3D<float4> GiShR0, GiShR1, GiShG0, GiShG1, GiShB0, GiShB1 (the two light sets); Texture3D<float> GiSkyVis;
//   a linear sampler for each (GiShR0Sampler ...); StructuredBuffer<GpuGiState> GiState.
// The finest level holding the point is read, from the set its parity names, blended into the next over the last
// fade voxels of its extent, and the point is pushed a voxel out along the normal first, so a surface reads the
// air in front of it and not the voxel it fills.

#ifndef MAGIC_GI_HLSLI
#define MAGIC_GI_HLSLI

#include "Gi/Common.hlsli"
#include "Gi/State.hlsli"

struct GiSample
{
    float3 irradiance; // the bounced light along the normal, before the intensity
    float skyVisibility;
    float weight;      // 0 where no level holds the point
};

// How well a level holds a voxel coordinate: 1 well inside, falling to 0 over the fade band at its edge.
float GiLevelWeight(float3 voxel, float fadeVoxels)
{
    float fade = max(fadeVoxels, 1.0);
    float3 edge = min(voxel, (float)GiResolution() - voxel);
    float nearest = min(edge.x, min(edge.y, edge.z));
    return saturate(nearest / fade);
}

float4 GiShSample(Texture3D<float4> set0, SamplerState sampler0, Texture3D<float4> set1, SamplerState sampler1, uint parity, float3 uv)
{
    return parity == 0u ? set0.SampleLevel(sampler0, uv, 0.0) : set1.SampleLevel(sampler1, uv, 0.0);
}

// The light sets hold light by the way it travels (Gi/Propagate.comp.hlsl), so a surface gathers what travels
// against its normal: what comes towards it, not what leaves it.
GiSample GiRead(uint level, float3 voxel, float3 normal)
{
    float3 towards = -normal;
    float3 uv = GiStackedUv(level, voxel);
    uint parity = GiParity(level);
    GiSample sample;
    sample.irradiance = float3(
        ShIrradiance(GiShSample(GiShR0, GiShR0Sampler, GiShR1, GiShR1Sampler, parity, uv), towards),
        ShIrradiance(GiShSample(GiShG0, GiShG0Sampler, GiShG1, GiShG1Sampler, parity, uv), towards),
        ShIrradiance(GiShSample(GiShB0, GiShB0Sampler, GiShB1, GiShB1Sampler, parity, uv), towards));
    sample.skyVisibility = GiSkyVis.SampleLevel(GiSkyVisSampler, uv, 0.0);
    sample.weight = 1.0;
    return sample;
}

// The volumes at a camera-relative position, along normal; weight 0 outside every level.
GiSample GiAt(float3 positionRel, float3 normal, float fadeVoxels)
{
    GiSample result;
    result.irradiance = float3(0.0, 0.0, 0.0);
    result.skyVisibility = 1.0;
    result.weight = 0.0;

    float remaining = 1.0;
    [loop] for (uint level = 0u; level < GiLevels() && remaining > 0.0; level++)
    {
        if (!GiLevelValid(level))
            continue;

        float3 voxel = (positionRel + (CameraPos - GiOrigin(level)) + normal * GiVoxel(level)) / GiVoxel(level);
        float weight = GiLevelWeight(voxel, fadeVoxels) * remaining;
        if (weight <= 0.0)
            continue;

        GiSample sample = GiRead(level, voxel, normal);
        result.irradiance += sample.irradiance * weight;
        result.skyVisibility = lerp(result.skyVisibility, sample.skyVisibility, result.weight == 0.0 ? 1.0 : weight);
        result.weight += weight;
        remaining -= weight;
    }

    return result;
}

#endif
