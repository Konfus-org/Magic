// Culls clusters for a view, a thread each: a group per job of the instance cull's, or a thread per cluster the first
// phase hid when it retests them. A cluster is drawn where its own error, projected from its group's sphere as near as
// it comes, covers no more pixels than the view allows, and its parent's does not (Karis 2021): every cluster decides
// alone, and as a group's clusters share their sphere and error, and each parent's are never less than its children's,
// the clusters drawn meet without cracks. The pixels allowed are the settings' times the budget's for the instance's
// distance from the player. Then it must be in view and not hidden behind the depth pyramid; the first phase keeps the
// hidden for the second, which tests them again against this frame's. What is drawn is noted with its raster bin and
// pieces, counted into its bin and its distance's bucket.

#include "Rendering/Include/Culling.hlsli"

CONSTANTS(CullConstants, c);

// The pixels an error of a cluster's covers from as near as its sphere comes.
float ErrorPixels(GpuInstance instance, float4 sphere, float error)
{
    float3 centre = InstancePosition(instance, sphere.xyz);
    float distance = max(length(centre) - sphere.w * instance.maxScale, View.near);
    return error * instance.maxScale * View.pixelScale / distance;
}

void Test(uint slot, uint index, uint bucket)
{
    GpuInstance instance = LoadInstance(slot);
    GpuCluster cluster = LoadCluster(index);
    uint bins = LoadMaterialBins(InstanceMaterial(instance, ClusterSlot(cluster)));
    if (!IsDrawnOpaque(bins))
    {
        return;
    }

    float allowed = View.clusterError * asfloat(BUFFER(c.budget).Load((Buckets + bucket) * 4u));
    if (ErrorPixels(instance, cluster.errorBounds, cluster.error) > allowed || ErrorPixels(instance, cluster.parentErrorBounds, cluster.parentError) <= allowed)
    {
        return;
    }

    float3 centre = InstancePosition(instance, cluster.bounds.xyz);
    float radius = cluster.bounds.w * instance.maxScale;
    if (!SphereInView(centre, radius))
    {
        return;
    }

    bool isHidden = c.phase == 1u
        ? IsHidden(c.hiZ, ToPrevClip(InstancePrevPosition(instance, cluster.bounds.xyz)), radius)
        : IsHidden(c.hiZ, ToClip(centre), radius);
    if (isHidden)
    {
        if (c.phase == 1u)
        {
            uint hidden = WaveAllocate(BUFFER(c.counts), CountHiddenClusters * 4u, 1u);
            if (hidden < CountOf(BUFFER(c.hiddenClusters), 8u))
            {
                BUFFER(c.hiddenClusters).Store2(hidden * 8u, uint2(slot, index | (bucket << 26)));
            }
        }

        return;
    }

    GpuModel model = LoadModel(instance.model);
    float takeover = CardTakeover(model, instance, length(InstancePosition(instance, model.bounds.xyz)));
    uint bin = BinOf(bins, Dissolves(DissolveOf(instance, takeover, false)), false);
    uint pieces = ClusterPieces(cluster);
    uint at = WaveAllocate(BUFFER(c.counts), CountClusters * 4u, 1u);
    if (at < CountOf(BUFFER(c.clusters), 16u))
    {
        BUFFER(c.clusters).Store4(at * 16u, uint4(slot, index, bin, pieces));
        BUFFER(c.binCounts).InterlockedAdd(bin * 4u, pieces);
        BUFFER(c.budget).InterlockedAdd(bucket * 4u, pieces);
    }
}

[numthreads(64, 1, 1)]
void main(uint3 group : SV_GroupID, uint thread : SV_GroupIndex)
{
    LoadView(c.view);
    if (c.phase == 3u)
    {
        uint index = LinearGroup(group) * 64u + thread;
        if (index >= min(BUFFER(c.counts).Load(CountHiddenClusters * 4u), CountOf(BUFFER(c.hiddenClusters), 8u)))
        {
            return;
        }

        uint2 hidden = BUFFER(c.hiddenClusters).Load2(index * 8u);
        Test(hidden.x, hidden.y & 0x3FFFFFFu, hidden.y >> 26);
        return;
    }

    uint job = LinearGroup(group) + (c.phase == 2u ? BUFFER(c.counts).Load(CountJobsFirst * 4u) : 0u);
    if (job >= min(BUFFER(c.counts).Load(CountJobs * 4u), CountOf(BUFFER(c.jobs), 16u)))
    {
        return;
    }

    uint4 work = BUFFER(c.jobs).Load4(job * 16u);
    if (thread < work.z)
    {
        Test(work.x, work.y + thread, work.w);
    }
}
