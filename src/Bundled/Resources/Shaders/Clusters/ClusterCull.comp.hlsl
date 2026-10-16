// Culls clusters for a view, a thread each: a group per job of the instance cull's, or a thread per cluster the first
// phase hid when it retests them. A cluster is drawn where its own error, projected from its group's sphere as near as
// it comes, covers no more pixels than the view allows, and its parent's does (Karis 2021): every cluster decides alone,
// and as a group's clusters share their sphere and error, and each parent's are never less than its children's, the
// clusters drawn meet without cracks. The pixels allowed are the settings' times the budget's for the instance's
// distance from the player. Then it must be in view and not hidden behind the depth pyramid; the first phase keeps
// the hidden for the second, which tests them again against this frame's. What is drawn is noted with its raster bin
// and pieces, counted into its bin and its distance's bucket.

#include "Include/Culling.hlsli"

struct PassParams
{
    // 0 for the first phase's jobs, 1 for the second's, 2 to retest what the first phase hid.
    uint mode = 0u;
};

// The pixels an error of a cluster's covers from as near as its sphere comes.
float ErrorPixels(GpuSceneInstance instance, float4 sphere, float error)
{
    float3 centre = ScenePosition(instance, sphere.xyz);
    float distance = max(length(centre) - sphere.w * instance.maxScale, Near);
    return error * instance.maxScale * PixelScale / distance;
}

void Test(uint slot, uint index, uint bucket, uint mode)
{
    GpuSceneInstance instance = LOAD(Instances, GpuSceneInstance, slot);
    GpuCluster cluster = LOAD(Clusters, GpuCluster, index);
    uint row = SceneMaterial(instance, ClusterSlot(cluster));
    if (row == 0xFFFFu)
    {
        return;
    }

    uint bins = MaterialBins.Load(row * 4u);
    if (!IsDrawnOpaque(bins))
    {
        return;
    }

    float allowed = ClusterError * asfloat(Budget.Load((Buckets + bucket) * 4u));
    if (ErrorPixels(instance, cluster.errorBounds, cluster.error) > allowed || ErrorPixels(instance, cluster.parentErrorBounds, cluster.parentError) <= allowed)
    {
        return;
    }

    float3 centre = ScenePosition(instance, cluster.bounds.xyz);
    float radius = cluster.bounds.w * instance.maxScale;
    if (!SphereInView(centre, radius))
    {
        return;
    }

    bool isHidden = mode == 0u
        ? IsHidden(mul(PrevViewProj, float4(ScenePrevPosition(instance, cluster.bounds.xyz), 1.0)), radius)
        : IsHidden(mul(ViewProj, float4(centre, 1.0)), radius);
    if (isHidden)
    {
        if (mode == 0u)
        {
            uint hidden = WaveAllocate(CullCounts, CountHiddenClusters * 4u, 1u);
            if (hidden < CountOf(HiddenClusters, 8u))
            {
                HiddenClusters.Store2(hidden * 8u, uint2(slot, index | (bucket << 26)));
            }
        }

        return;
    }

    GpuModel model = LOAD(Models, GpuModel, instance.model);
    float takeover = CardTakeover(model, instance, length(ScenePosition(instance, model.bounds.xyz)));
    uint bin = BinOf(bins, Dissolves(DissolveOf(instance, takeover, false)), false);
    uint pieces = ClusterPieces(cluster);
    uint at = WaveAllocate(CullCounts, CountClusters * 4u, 1u);
    if (at < CountOf(VisibleClusters, 16u))
    {
        VisibleClusters.Store4(at * 16u, uint4(slot, index, bin, pieces));
        BinCounts.InterlockedAdd(bin * 4u, pieces);
        Budget.InterlockedAdd(bucket * 4u, pieces);
    }
}

[numthreads(64, 1, 1)]
void main(uint3 group : SV_GroupID, uint thread : SV_GroupIndex)
{
    PassParams params = LoadPassParams();
    if (params.mode == 2u)
    {
        uint index = LinearGroup(group) * 64u + thread;
        if (index >= min(CullCounts.Load(CountHiddenClusters * 4u), CountOf(HiddenClusters, 8u)))
        {
            return;
        }

        uint2 hidden = HiddenClusters.Load2(index * 8u);
        Test(hidden.x, hidden.y & 0x3FFFFFFu, hidden.y >> 26, 2u);
        return;
    }

    uint job = LinearGroup(group) + (params.mode == 1u ? CullCounts.Load(CountJobsFirst * 4u) : 0u);
    if (job >= min(CullCounts.Load(CountJobs * 4u), CountOf(Jobs, 16u)))
    {
        return;
    }

    uint4 work = Jobs.Load4(job * 16u);
    if (thread < work.z)
    {
        Test(work.x, work.y + thread, work.w, params.mode);
    }
}
