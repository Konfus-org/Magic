// The scene's field made again from its models: each group takes every GiJobGroups-th job, its threads each voxel the
// job reaches in a brick made again, and keeps the nearest distance of every model there (an atomic minimum, as the
// union of shapes is the least of their distances).

#include "Probes/GiJobs.hlsli"

[numthreads(64, 1, 1)]
void main(uint3 group : SV_GroupID, uint thread : SV_GroupIndex)
{
    uint count = min(GiJobs.Load(0u), CountOf(GiJobs, 8u) - 1u);
    [loop] for (uint index = group.x; index < count; index += GiJobGroups)
    {
        GiJob job = GiJobOf(index);
        uint voxels = GiJobVoxels(job);
        [loop] for (uint at = thread; at < voxels; at += 64u)
        {
            int3 voxel = GiJobVoxel(job, at);
            if (!GiIsDirty(job.info, voxel))
            {
                continue;
            }

            uint ignored;
            GiSplat.InterlockedMin(GiIndex(GiStored(voxel, job.level)) * 4u, GiEncode(GiJobDistance(job, voxel)), ignored);
        }
    }
}
