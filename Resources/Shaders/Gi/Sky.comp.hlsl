// How much sky each voxel of the level sees: a few fixed cones towards the sky, each sphere-traced through the
// distance field by its own level and on into the coarser levels when it leaves this one, so a cave roof far
// above still closes the sky over the finest level. One thread per voxel. An occupied voxel traces from the air
// beside it (a surface's filtered read reaches into the voxel it fills, which would otherwise read black); one with
// no air beside it sees none.

#include "Include/Frame.hlsli"
#include "Gi/Common.hlsli"

#define CONES 5
#define STEPS 10

Texture3D<float4> Albedo : READ(0);
SamplerState AlbedoSampler : SAMPLER(0);
Texture3D<float> Sdf : READ(1);
SamplerState SdfSampler : SAMPLER(1);

[[vk::image_format("r8")]]
RWTexture3D<float> SkyVis : WRITE(0);
StructuredBuffer<GpuGiState> GiState : READ(2);

#include "Gi/State.hlsli"

static const float3 ConeDirections[CONES] =
{
    float3(0.0, 1.0, 0.0), float3(0.7071, 0.7071, 0.0), float3(-0.7071, 0.7071, 0.0), float3(0.0, 0.7071, 0.7071), float3(0.0, 0.7071, -0.7071)
};
static const float ConeWeights[CONES] = { 1.0, 0.7, 0.7, 0.7, 0.7 };
static const float ConeTangent = 0.45;

static const int3 Neighbours[6] = { int3(1, 0, 0), int3(-1, 0, 0), int3(0, 1, 0), int3(0, -1, 0), int3(0, 0, 1), int3(0, 0, -1) };

// The finest valid level from `from` on whose volume holds the point; GiLevels() when none does. A cone only ever
// moves out of a level into a coarser one, so its trace starts each step from the level of the step before.
uint LevelHolding(float3 world, uint from)
{
    uint level = from;
    [loop] for (; level < GiLevels(); level++)
    {
        float3 voxel = GiVoxelCoord(level, world);
        bool inside = all(voxel >= 1.0) && all(voxel <= (float)GiResolution() - 1.0);
        if (inside && GiLevelValid(level))
            break;
    }

    return level;
}

// The field's distance at a world point, in metres, from the finest level from `level` on that holds it (which it
// leaves in `level`), and that level's voxel; the sky's when none does.
float DistanceAt(float3 world, inout uint level, out bool inSky, out float levelVoxel)
{
    level = LevelHolding(world, level);
    inSky = level == GiLevels();
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
    uint level = 0u;
    [loop] for (uint step = 0u; step < STEPS; step++)
    {
        bool inSky;
        float levelVoxel;
        float distance = DistanceAt(start + direction * travelled, level, inSky, levelVoxel);
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

float CoverageAt(uint level, int3 voxel)
{
    return Albedo.SampleLevel(AlbedoSampler, GiStackedUv(level, (float3)voxel + 0.5), 0.0).a;
}

// Where a voxel's cones start: its centre when it is air; for an occupied one the centre pushed a voxel out towards
// the air around it, false when there is none.
bool ConeStart(uint level, int3 voxel, float voxelSize, out float3 start)
{
    start = GiOrigin(level) + ((float3)voxel + 0.5) * voxelSize;
    if (CoverageAt(level, voxel) < GiOccupied)
        return true;

    int res = (int)GiResolution();
    float3 outward = float3(0.0, 0.0, 0.0);
    [unroll] for (uint n = 0u; n < 6u; n++)
    {
        int3 other = voxel + Neighbours[n];
        if (all(other >= 0) && all(other < res) && CoverageAt(level, other) < GiOccupied)
            outward += (float3)Neighbours[n];
    }

    if (dot(outward, outward) < 0.5)
        return false;

    start += normalize(outward) * voxelSize;
    return true;
}

[numthreads(4, 4, 4)]
void main(uint3 threadId : SV_DispatchThreadID)
{
    if (!GiRebuilding() || any(threadId >= GiResolution()))
        return;

    uint level = GiUpdateLevel();
    int3 texel = GiStackedTexel(level, threadId);
    float voxel = GiVoxel(level);
    float sky = 0.0;
    float3 start;
    [branch] if (ConeStart(level, (int3)threadId, voxel, start))
    {
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
