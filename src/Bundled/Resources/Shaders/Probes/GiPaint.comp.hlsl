// What the scene's surfaces are made of: the jobs again, each voxel within one of a surface taking the colour and light
// of the model whose distance is the field's there, the nearest one's.

#include "Probes/GiJobs.hlsli"

[numthreads(64, 1, 1)]
void main(uint3 group : SV_GroupID, uint thread : SV_GroupIndex)
{
    uint count = min(GiJobs.Load(0u), CountOf(GiJobs, 8u) - 1u);
    [loop] for (uint index = group.x; index < count; index += GiJobGroups)
    {
        GiJob job = GiJobOf(index);
        if (job.material == 0xFFFFu)
        {
            continue;
        }

        GpuMaterialGi material = LOAD(MaterialGi, GpuMaterialGi, job.material);
        uint voxels = GiJobVoxels(job);
        [loop] for (uint at = thread; at < voxels; at += 64u)
        {
            int3 voxel = GiJobVoxel(job, at);
            if (!GiIsDirty(job.info, voxel))
            {
                continue;
            }

            float distance = GiJobDistance(job, voxel);
            uint3 stored = GiStored(voxel, job.level);
            if (abs(distance) < job.info.voxel && GiEncode(distance) == GiSplat.Load(GiIndex(stored) * 4u))
            {
                GiSurface[stored] = float4(material.color.rgb, 1.0);
                GiEmission[stored] = float4(material.emission.rgb, 0.0);
            }
        }
    }
}
