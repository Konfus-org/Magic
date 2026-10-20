// Finds what may cast into the shadow pages drawn this frame, a thread an instance, read once and held against each
// shadow group (ShadowAllocate) in turn: an instance whose bounds reach the rectangle a sun page's view draws, or a
// light's reach, hands the levels of its model's clusters whose error can suit the group's texels to the shadows'
// cluster cull, in jobs of 64. A sun level's texels are the same size everywhere, so one level of clusters suits it; a
// light's grow with distance from it, and the face's finest mip drawn sets them. Cards cast no shadow: an instance far
// enough for its cards casts with its clusters, as coarse as the shadow's texels allow.

#include "Include/Culling.hlsli"
#include "Include/VirtualShadows.hlsli"

void Cull(uint slot, GpuSceneInstance instance, GpuModel model, float3 centre, float radius, uint shadowGroup, GpuShadowGroup target)
{
    // Pixels of error a metre of the model's is, as near and as far as its clusters' error spheres reach.
    float perMetre;
    float nearest = 1.0;
    float farthest = 1.0;
    if ((target.target & LightOwner) == 0u)
    {
        if (!ReachesViewRect(LOAD(ShadowViews, GpuShadowView, target.view), centre, radius))
        {
            return;
        }

        perMetre = instance.maxScale / target.texel;
    }
    else
    {
        float distance = length(centre - target.light);
        if (distance - radius > target.range)
        {
            return;
        }

        perMetre = instance.maxScale * (float)FaceTexels(min(FinestMip(target.faceMips), LightMips - 1u)) * 0.5;
        float reach = model.clusterReach * instance.maxScale;
        nearest = max(distance - reach, LightNear);
        farthest = max(distance + reach, LightNear);
    }

    // The levels whose clusters the group can want somewhere on the model, a run, as the view's cull finds them.
    uint first = 0xFFFFFFFFu;
    uint last = 0u;
    [loop] for (uint level = 0u; level < model.levelCount; level++)
    {
        GpuLevel row = LOAD(Levels, GpuLevel, model.firstLevel + level);
        bool isFine = row.minError * perMetre / farthest <= ClusterError;
        bool isParentCoarse = row.maxParentError * perMetre / nearest > ClusterError;
        if (isFine && isParentCoarse)
        {
            first = min(first, row.firstCluster);
            last = max(last, row.firstCluster + row.count);
        }
    }

    if (first >= last)
    {
        return;
    }

    uint jobs = (last - first + JobClusters - 1u) / JobClusters;
    uint at = WaveAllocate(ShadowCounts, ShadowCountJobs, jobs);
    uint capacity = CountOf(ShadowJobs, 16u);
    [loop] for (uint job = 0u; job < jobs && at + job < capacity; job++)
    {
        uint start = first + job * JobClusters;
        ShadowJobs.Store4((at + job) * 16u, uint4(slot, start, min(JobClusters, last - start), shadowGroup));
    }
}

[numthreads(64, 1, 1)]
void main(uint3 group : SV_GroupID, uint thread : SV_GroupIndex)
{
    uint instances = CountOf(Instances, GpuSceneInstanceBytes);
    uint shadowGroups = min(ShadowCounts.Load(ShadowCountGroups), CountOf(ShadowGroups, 32u));

    // A row runs MaxGroupsX groups at most; past them, each thread takes the next instance a row's width on.
    [loop] for (uint slot = group.x * 64u + thread; slot < instances; slot += MaxGroupsX * 64u)
    {
        GpuSceneInstance instance = LOAD(Instances, GpuSceneInstance, slot);
        if (instance.maxScale <= 0.0)
        {
            continue;
        }

        GpuModel model = LOAD(Models, GpuModel, instance.model);
        if (model.clusterCount == 0u)
        {
            continue;
        }

        float3 centre = ScenePosition(instance, model.bounds.xyz);
        float radius = model.bounds.w * instance.maxScale;
        [loop] for (uint shadowGroup = 0u; shadowGroup < shadowGroups; shadowGroup++)
        {
            Cull(slot, instance, model, centre, radius, shadowGroup, LOAD(ShadowGroups, GpuShadowGroup, shadowGroup));
        }
    }
}
