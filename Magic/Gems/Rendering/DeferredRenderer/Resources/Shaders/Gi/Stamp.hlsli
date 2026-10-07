// What CollectInstances and Stamp agree on: an instance's footprint in the level being rebuilt (the voxels its
// mesh box, transformed, can touch), how that footprint is cut into GI_TASK_CHUNK^3 chunks, and how a voxel takes
// a share of an instance (the accumulation buffer, atomics: counts of coverage and colour, the brightest emission).

#ifndef MAGIC_GI_STAMP_HLSLI
#define MAGIC_GI_STAMP_HLSLI

#include "Gi/Common.hlsli"

// The world-space box of a mesh box under an instance's transform: the eight corners, transformed, and their extent.
void InstanceWorldBox(GpuInstanceXform world, float3 boxMin, float3 boxMax, out float3 worldMin, out float3 worldMax)
{
    worldMin = float3(1e30, 1e30, 1e30);
    worldMax = -worldMin;
    [unroll] for (uint corner = 0u; corner < 8u; corner++)
    {
        float3 local = float3((corner & 1u) != 0u ? boxMax.x : boxMin.x, (corner & 2u) != 0u ? boxMax.y : boxMin.y, (corner & 4u) != 0u ? boxMax.z : boxMin.z);
        float4 point4 = float4(local, 1.0);
        float3 at = float3(dot(world.r0, point4), dot(world.r1, point4), dot(world.r2, point4));
        worldMin = min(worldMin, at);
        worldMax = max(worldMax, at);
    }
}

// The footprint's first and last voxel in the level, and how many chunks it is cut into per axis. False when the
// box misses the level.
bool Footprint(uint level, float3 worldMin, float3 worldMax, out int3 first, out int3 last, out uint3 chunks)
{
    float voxel = GiVoxel(level);
    float3 origin = GiOrigin(level);
    int res = (int)GiResolution();
    first = clamp((int3)floor((worldMin - origin) / voxel), 0, res - 1);
    last = clamp((int3)floor((worldMax - origin) / voxel), 0, res - 1);
    bool inside = all(worldMax >= origin) && all(worldMin <= origin + GiExtent(level));
    chunks = (uint3)(last - first + (int)GI_TASK_CHUNK) / GI_TASK_CHUNK;
    return inside && all(last >= first);
}

// The inverse of an instance's transform applied to a world point: the mesh-space point.
float3 ToMeshSpace(GpuInstanceXform world, float3 at)
{
    float3 row0 = world.r0.xyz, row1 = world.r1.xyz, row2 = world.r2.xyz;
    float3 translation = float3(world.r0.w, world.r1.w, world.r2.w);
    float3 c0 = cross(row1, row2), c1 = cross(row2, row0), c2 = cross(row0, row1);
    float determinant = dot(row0, c0);
    float3 moved = at - translation;

    // inverse(A) = transpose(cofactors) / det: the columns of the inverse are the cofactor rows.
    return float3(dot(c0, moved), dot(c1, moved), dot(c2, moved)) / (abs(determinant) > 1e-12 ? determinant : 1e-12);
}

void Accumulate(RWStructuredBuffer<uint> accum, uint voxel, float coverage, float3 albedo, float3 emissive)
{
    uint row = voxel * GI_ACCUM_WORDS;
    uint share = (uint)(saturate(coverage) * 255.0 + 0.5);
    uint3 color = (uint3)(saturate(albedo) * (float)share);
    InterlockedAdd(accum[row], share);
    InterlockedAdd(accum[row + 1u], color.x);
    InterlockedAdd(accum[row + 2u], color.y);
    InterlockedAdd(accum[row + 3u], color.z);
    [branch] if (any(emissive > 0.0))
    {
        InterlockedMax(accum[row + 4u], asuint(max(emissive.x, 0.0)));
        InterlockedMax(accum[row + 5u], asuint(max(emissive.y, 0.0)));
        InterlockedMax(accum[row + 6u], asuint(max(emissive.z, 0.0)));
    }
}

#endif
