// How much sky each voxel of the level sees: a few fixed cones towards the sky, each sphere-traced through the
// distance field by its own level and on into the coarser levels when it leaves this one, so a cave roof far
// above still closes the sky over the finest level. An occupied voxel sees none. One thread per voxel.

#include "Include/Shade.hlsli"
#include "Gi/Common.hlsli"

#define CONES 5
#define STEPS 10

Texture3D<float4> Albedo : READ(0);
SamplerState AlbedoSampler : SAMPLER(0);
Texture3D<float> Sdf : READ(1);
SamplerState SdfSampler : SAMPLER(1);

[[vk::image_format("r8")]]
RWTexture3D<float> SkyVis : WRITE(0);

static const float3 ConeDirections[CONES] =
{
    float3(0.0, 1.0, 0.0), float3(0.7071, 0.7071, 0.0), float3(-0.7071, 0.7071, 0.0), float3(0.0, 0.7071, 0.7071), float3(0.0, 0.7071, -0.7071)
};
static const float ConeWeights[CONES] = { 1.0, 0.7, 0.7, 0.7, 0.7 };
static const float ConeTangent = 0.45;

// The finest valid level whose volume holds the point; GiLevels when none does.
uint LevelHolding(float3 world)
{
    uint found = GiLevels;
    [loop] for (uint level = 0u; level < GiLevels; level++)
    {
        float3 voxel = GiVoxelCoord(level, world);
        bool inside = all(voxel >= 1.0) && all(voxel <= (float)GiResolution - 1.0);
        found = (found == GiLevels && inside && GiLevelValid(level)) ? level : found;
    }

    return found;
}

// The field's distance at a world point, in metres, from the finest level that holds it, and that level's voxel;
// the sky's when none does.
float DistanceAt(float3 world, out bool inSky, out float levelVoxel)
{
    uint level = LevelHolding(world);
    inSky = level == GiLevels;
    levelVoxel = inSky ? 0.0 : GiVoxel(level);
    if (inSky)
        return 1e9;

    float3 voxel = GiVoxelCoord(level, world);
    return Sdf.SampleLevel(SdfSampler, GiStackedUv(level, voxel), 0.0) * GiSdfMaxVoxels * levelVoxel;
}

// Within this many voxels of a surface the trace has hit it: the filtered field reads half a voxel at a voxel's face
// and never quite zero between samples, so a wall one voxel thick would otherwise only dim the cone.
static const float HitVoxels = 0.6;

float ConeVisibility(float3 start, float3 direction, float voxel)
{
    float visibility = 1.0;
    float travelled = voxel;
    [loop] for (uint step = 0u; step < STEPS; step++)
    {
        bool inSky;
        float levelVoxel;
        float distance = DistanceAt(start + direction * travelled, inSky, levelVoxel);
        if (inSky)
            break;

        if (distance < HitVoxels * levelVoxel)
        {
            visibility = 0.0;
            break;
        }

        visibility = min(visibility, saturate(distance / (ConeTangent * travelled)));
        travelled += max(distance, levelVoxel);
    }

    return visibility;
}

[numthreads(4, 4, 4)]
void main(uint3 threadId : SV_DispatchThreadID)
{
    if (any(threadId >= GiResolution))
        return;

    uint level = GiUpdateLevel;
    int3 texel = GiStackedTexel(level, threadId);
    float coverage = Albedo.SampleLevel(AlbedoSampler, GiStackedUv(level, (float3)threadId + 0.5), 0.0).a;
    float sky = 0.0;
    [branch] if (coverage < GiOccupied)
    {
        float voxel = GiVoxel(level);
        float3 start = GiOrigin(level) + ((float3)threadId + 0.5) * voxel;
        float total = 0.0;
        [unroll] for (uint cone = 0u; cone < CONES; cone++)
        {
            sky += ConeWeights[cone] * ConeVisibility(start, ConeDirections[cone], voxel);
            total += ConeWeights[cone];
        }

        sky /= total;
    }

    SkyVis[texel] = sky;
}
