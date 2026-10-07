// One group per listed page, one thread per instance slot of it: an alive, shown instance whose bounds touch the
// level being rebuilt is stamped into it. One that covers at most a voxel (most of a big world's instances at the
// coarser levels) takes its share of that voxel here and now; a bigger one is cut into GI_TASK_CHUNK^3 chunks of
// its footprint, one task each for Stamp.comp.hlsl (TaskArgs, reset by the CPU to (0, 1, 1, 0), counts them).

#include "Include/Frame.hlsli"
#include "Gi/Common.hlsli"

StructuredBuffer<uint> PageList : READ(0);
StructuredBuffer<GpuPage> Pages : READ(1);
StructuredBuffer<GpuInstance> Instances : READ(2);
StructuredBuffer<GpuInstanceXform> Xforms : READ(3);
StructuredBuffer<GpuGiGroup> Groups : READ(4);
StructuredBuffer<GpuGiMaterial> Materials : READ(5);
RWStructuredBuffer<uint> Accum : WRITE(0);
RWStructuredBuffer<uint2> Tasks : WRITE(1);
RWStructuredBuffer<uint> TaskArgs : WRITE(2);
RWStructuredBuffer<uint> Counters : WRITE(3);
StructuredBuffer<GpuGiState> GiState : READ(6);

#include "Gi/State.hlsli"
#include "Gi/Stamp.hlsli"

static const uint MaxChunksPerInstance = 32768u;

[numthreads(256, 1, 1)]
void main(uint3 groupId : SV_GroupID, uint3 groupThreadId : SV_GroupThreadID)
{
    GpuPage page = Pages[PageList[groupId.x]];
    if (groupThreadId.x >= page.count)
        return;

    uint slot = page.firstInstance + groupThreadId.x;
    GpuInstance instance = Instances[slot];
    if ((instance.flags & (InstanceAlive | InstanceHidden)) != InstanceAlive)
        return;

    uint level = GiUpdateLevel();
    float voxel = GiVoxel(level);
    float3 levelMin = GiOrigin(level);
    float3 levelMax = levelMin + GiExtent(level);
    float3 center = instance.sphere.xyz;
    float radius = instance.sphere.w;
    if (any(center + radius < levelMin) || any(center - radius > levelMax))
        return;

    GpuGiGroup group = Groups[instance.bucketGroup];
    GpuGiMaterial material = Materials[instance.materialSlot];
    float3 worldMin, worldMax;
    InstanceWorldBox(Xforms[slot], group.boxMin.xyz, group.boxMax.xyz, worldMin, worldMax);

    int3 first, last;
    uint3 chunks;
    if (!Footprint(level, worldMin, worldMax, first, last, chunks))
        return;

    // Small enough for one voxel, or without a brick: the sphere's share of the voxel it is in.
    float3 extent = worldMax - worldMin;
    bool small = all(extent <= voxel) || group.brick == GiNoBrick;
    [branch] if (small)
    {
        float volume = 4.18879 * radius * radius * radius;
        float coverage = saturate(volume / (voxel * voxel * voxel));
        uint3 at = (uint3)clamp((int3)floor((center - levelMin) / voxel), 0, (int)GiResolution() - 1);
        Accumulate(Accum, GiVoxelIndex(at), coverage, material.albedo.rgb, material.emissive.rgb);
        return;
    }

    uint count = chunks.x * chunks.y * chunks.z;
    if (count > MaxChunksPerInstance)
    {
        InterlockedAdd(Counters[1], 1u);
        return;
    }

    [loop] for (uint chunk = 0u; chunk < count; chunk++)
    {
        uint index;
        InterlockedAdd(TaskArgs[0], 1u, index);
        if (index >= GI_TASK_CAPACITY)
        {
            InterlockedAdd(Counters[0], 1u);
            break;
        }

        Tasks[index] = uint2(slot, chunk);
    }
}
