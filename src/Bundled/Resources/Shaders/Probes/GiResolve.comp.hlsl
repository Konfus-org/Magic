// The last of making the scene's field again: each voxel in a brick made again takes the nearest distance the models
// left it, as a value the field is read through filtered.

#include "Lighting/Gi.hlsli"

[numthreads(64, 1, 1)]
void main(uint3 thread : SV_DispatchThreadID)
{
    uint3 stored = uint3(thread.x % GiResolution, (thread.x / GiResolution) % GiResolution, thread.x / (GiResolution * GiResolution));
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

    float reach = GiReach * level.voxel;
    GiField[stored] = clamp(GiDecode(GiSplat.Load(thread.x * 4u)), -reach, reach);
}
