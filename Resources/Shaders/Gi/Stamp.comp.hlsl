// One group per task, one thread per voxel of its GI_TASK_CHUNK^3 chunk of an instance's footprint: the voxel's
// cube is taken back into the mesh's space and the cells of the mesh's occupancy brick it covers are looked at (up
// to four a side, spread over the cube); where the mesh is, the voxel takes the instance's share (its material's
// albedo and emission) by how much of it the mesh fills, pushed up so a wall thinner than a voxel still fills it
// but a small thing in a big voxel of a coarse level does not. Dispatched indirectly from TaskArgs; a task index
// past the capacity is never written, so the dispatch is clamped on the CPU side by the buffer's size.

#include "Include/Shade.hlsli"
#include "Gi/Stamp.hlsli"

Texture3D<float> BrickAtlas : READ(0);
SamplerState BrickAtlasSampler : SAMPLER(0);
StructuredBuffer<uint2> Tasks : READ(1);
StructuredBuffer<GpuInstance> Instances : READ(2);
StructuredBuffer<GpuInstanceXform> Xforms : READ(3);
StructuredBuffer<GpuGiGroup> Groups : READ(4);
StructuredBuffer<GpuGiMaterial> Materials : READ(5);
RWStructuredBuffer<uint> Accum : WRITE(0);

[numthreads(GI_TASK_CHUNK, GI_TASK_CHUNK, GI_TASK_CHUNK)]
void main(uint3 groupId : SV_GroupID, uint3 groupThreadId : SV_GroupThreadID)
{
    if (groupId.x >= GI_TASK_CAPACITY)
        return;

    uint2 task = Tasks[groupId.x];
    uint slot = task.x;
    GpuInstance instance = Instances[slot];
    GpuInstanceXform world = Xforms[slot];
    GpuGiGroup group = Groups[instance.bucketGroup];

    uint level = GiUpdateLevel;
    float3 worldMin, worldMax;
    InstanceWorldBox(world, group.boxMin.xyz, group.boxMax.xyz, worldMin, worldMax);
    int3 first, last;
    uint3 chunks;
    if (!Footprint(level, worldMin, worldMax, first, last, chunks))
        return;

    uint3 chunk = uint3(task.y % chunks.x, (task.y / chunks.x) % chunks.y, task.y / (chunks.x * chunks.y));
    int3 voxel = first + (int3)(chunk * GI_TASK_CHUNK + groupThreadId);
    if (any(voxel > last))
        return;

    // The voxel's cube in brick cells: its corners through the inverse transform.
    float voxelSize = GiVoxel(level);
    float3 origin = GiOrigin(level);
    float3 size = max(group.boxMax.xyz - group.boxMin.xyz, 1e-4);
    float3 cellMin = float3(1e30, 1e30, 1e30), cellMax = -cellMin;
    [unroll] for (uint corner = 0u; corner < 8u; corner++)
    {
        float3 offset = float3(corner & 1u, (corner >> 1u) & 1u, (corner >> 2u) & 1u);
        float3 local = ToMeshSpace(world, origin + ((float3)voxel + offset) * voxelSize);
        float3 cell = (local - group.boxMin.xyz) / size * (float)GI_BRICK;
        cellMin = min(cellMin, cell);
        cellMax = max(cellMax, cell);
    }

    if (any(cellMax < 0.0) || any(cellMin > (float)GI_BRICK))
        return;

    // How much of the cube the mesh's box takes, then the cells of the box inside it, a few a side.
    float3 clampedMin = max(cellMin, 0.0), clampedMax = min(cellMax, (float)GI_BRICK);
    float3 share = (clampedMax - clampedMin) / max(cellMax - cellMin, 1e-4);
    float boxShare = share.x * share.y * share.z;
    int3 cellFirst = clamp((int3)floor(clampedMin), 0, (int)GI_BRICK - 1);
    int3 cellLast = clamp((int3)floor(clampedMax - 1e-4), 0, (int)GI_BRICK - 1);
    int3 stride = max((cellLast - cellFirst + 4) / 4, 1);
    float hits = 0.0, samples = 0.0;
    [loop] for (int z = cellFirst.z; z <= cellLast.z; z += stride.z)
    {
        [loop] for (int y = cellFirst.y; y <= cellLast.y; y += stride.y)
        {
            [loop] for (int x = cellFirst.x; x <= cellLast.x; x += stride.x)
            {
                samples += 1.0;
                hits += BrickAtlas.SampleLevel(BrickAtlasSampler, GiBrickUv(group.brick, (float3(x, y, z) + 0.5) / (float)GI_BRICK), 0.0) > 0.5 ? 1.0 : 0.0;
            }
        }
    }

    // Any cell hit makes the voxel solid once the mesh takes a real share of it: a wall thinner than the voxel still
    // blocks, a pole in a big voxel of a coarse level does not.
    float fill = boxShare * hits / max(samples, 1.0);
    if (hits == 0.0 || fill < 0.02)
        return;

    float coverage = max(GiOccupied + 0.1, saturate(3.0 * fill));

    GpuGiMaterial material = Materials[instance.materialSlot];
    Accumulate(Accum, GiVoxelIndex((uint3)voxel), coverage, material.albedo.rgb, material.emissive.rgb);
}
