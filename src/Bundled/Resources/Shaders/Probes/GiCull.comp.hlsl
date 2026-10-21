// Which instances the scene's field is made again from: a thread an instance, and for one of a model with a field, a
// job for each level whose made-again bricks its field reaches.

#include "Probes/GiJobs.hlsli"

[numthreads(64, 1, 1)]
void main(uint3 thread : SV_DispatchThreadID)
{
    if (thread.x >= CountOf(Instances, GpuInstanceBytes))
    {
        return;
    }

    GpuInstance instance = LOAD(Instances, GpuInstance, thread.x);
    if (instance.maxScale <= 0.0)
    {
        return;
    }

    uint field = LOAD(Models, GpuModel, instance.model).field;
    if (field == 0u)
    {
        return;
    }

    float3 centre;
    float3 extent;
    GiWorldBox(instance, LOAD(MeshFields, GpuMeshField, field), centre, extent);
    uint capacity = CountOf(GiJobs, 8u);
    uint levels = GiHeader().levels;
    [loop] for (uint index = 0u; index < levels; index++)
    {
        GpuGiLevel level = GiLevel(index);
        int3 low;
        int3 high;
        GiReachOf(level, centre, extent, low, high);
        if (level.voxel <= 0.0 || any(high < level.dirtyMin) || any(low > level.dirtyMax))
        {
            continue;
        }

        uint slot = WaveAllocate(GiJobs, 0u, 1u);
        if (1u + slot < capacity)
        {
            STORE(GiJobs, uint2, 1u + slot, uint2(thread.x, index));
        }
    }
}
