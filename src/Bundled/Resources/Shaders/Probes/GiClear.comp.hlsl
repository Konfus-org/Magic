// The first of making the scene's field again: a thread a voxel of every level's volume, each in a brick made again
// this frame set as far as the field says and with no surface, and the count of jobs set to none.

#include "Lighting/Gi.hlsli"

[numthreads(64, 1, 1)]
void main(uint3 thread : SV_DispatchThreadID)
{
    if (thread.x == 0u)
    {
        GiJobs.Store(0u, 0u);
    }

    uint3 stored = GiStoredAt(thread.x);
    uint index = stored.z / GiResolution;
    if (index >= GiHeader().levels)
    {
        return;
    }

    GpuGiLevel level = GiLevel(index);
    if (!GiIsDirty(level, GiVoxelAt(level, stored)))
    {
        return;
    }

    GiSplat.Store(thread.x * 4u, GiEncode(GiReach * level.voxel));
    GiSurface[stored] = float4(0.0, 0.0, 0.0, 0.0);
    GiEmission[stored] = float4(0.0, 0.0, 0.0, 0.0);
}
