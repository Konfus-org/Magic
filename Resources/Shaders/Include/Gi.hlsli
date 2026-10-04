// How a pixel reads the GI volumes: the sky it sees, the interior tint where it does not, and the bounced light
// gathered along a normal. Needs Include/Shade.hlsli and these declared by the including shader:
//   Texture3D<float4> GiShR, GiShG, GiShB; Texture3D<float> GiSkyVis; and a linear sampler for each (GiShRSampler ...).
// The finest level holding the point is read, blended into the next over the last SkyColor.w voxels of its extent,
// and the point is pushed a voxel out along the normal first, so a surface reads the air in front of it and not
// the voxel it fills.

#ifndef MAGIC_GI_HLSLI
#define MAGIC_GI_HLSLI

#include "Include/Shade.hlsli"
#include "Gi/Common.hlsli"

struct GiSample
{
    float3 irradiance; // the bounced light along the normal, before the intensity
    float skyVisibility;
    float weight;      // 0 where no level holds the point
};

// How well a level holds a voxel coordinate: 1 well inside, falling to 0 over the fade band at its edge.
float GiLevelWeight(float3 voxel)
{
    float fade = max(SkyColor.w, 1.0);
    float3 edge = min(voxel, (float)GiResolution - voxel);
    float nearest = min(edge.x, min(edge.y, edge.z));
    return saturate(nearest / fade);
}

GiSample GiRead(uint level, float3 voxel, float3 normal)
{
    float3 uv = GiStackedUv(level, voxel);
    GiSample sample;
    sample.irradiance = float3(
        ShIrradiance(GiShR.SampleLevel(GiShRSampler, uv, 0.0), normal),
        ShIrradiance(GiShG.SampleLevel(GiShGSampler, uv, 0.0), normal),
        ShIrradiance(GiShB.SampleLevel(GiShBSampler, uv, 0.0), normal));
    sample.skyVisibility = GiSkyVis.SampleLevel(GiSkyVisSampler, uv, 0.0);
    sample.weight = 1.0;
    return sample;
}

// The volumes at a camera-relative position, along normal; weight 0 outside every level.
GiSample GiAt(float3 positionRel, float3 normal)
{
    GiSample result;
    result.irradiance = float3(0.0, 0.0, 0.0);
    result.skyVisibility = 1.0;
    result.weight = 0.0;

    float remaining = 1.0;
    [loop] for (uint level = 0u; level < GiLevels && remaining > 0.0; level++)
    {
        if (!GiLevelValid(level))
            continue;

        float3 voxel = (positionRel + ClipmapCameraOffset[level].xyz + normal * GiVoxel(level)) / GiVoxel(level);
        float weight = GiLevelWeight(voxel) * remaining;
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
