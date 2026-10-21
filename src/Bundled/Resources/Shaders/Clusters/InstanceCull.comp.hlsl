// Culls the world's instances for a view, a thread each: every slot in the first phase, those the first phase hid in
// the second. An instance in view and not hidden behind the depth pyramid (last frame's, seen through last frame's view
// as it stood then, in the first phase; this frame's in the second) hands its model's clusters to the cluster cull: only
// the levels whose error, projected from as near and as far as the model reaches, can be what the view wants there, in
// jobs of 64. Its cards go straight to the pieces, from where they take over from its clusters on, the two dissolving
// into each other across a band. One the first phase hid waits for the second.

#include "Include/Culling.hlsli"

struct PassParams
{
    // 1 for the first phase, 2 for the second.
    uint phase = 1u;
};

void AddCards(uint slot, GpuInstance instance, GpuModel model, float takeover, uint bucket)
{
    float2 dissolve = DissolveOf(instance, takeover, true);
    [loop] for (uint i = 0u; i < model.cardCount; i++)
    {
        GpuCard card = LOAD(Cards, GpuCard, model.firstCard + i);
        uint row = InstanceMaterial(instance, card.slot);
        if (row == 0xFFFFu)
        {
            continue;
        }

        uint bins = MaterialBins.Load(row * 4u);
        if (!IsDrawnOpaque(bins))
        {
            continue;
        }

        uint bin = BinOf(bins, Dissolves(dissolve), true);
        uint at = WaveAllocate(CullCounts, CountClusters * 4u, 1u);
        if (at < CountOf(VisibleClusters, 16u))
        {
            VisibleClusters.Store4(at * 16u, uint4(slot, (model.firstCard + i) | CardBit, bin, 1u));
            BinCounts.InterlockedAdd(bin * 4u, 1u);
            Budget.InterlockedAdd(bucket * 4u, 1u);
        }
    }
}

[numthreads(64, 1, 1)]
void main(uint3 group : SV_GroupID, uint thread : SV_GroupIndex)
{
    PassParams params = LoadPassParams();
    uint slot = LinearGroup(group) * 64u + thread;
    if (params.phase == 2u)
    {
        if (slot >= CullCounts.Load(CountHiddenInstances * 4u))
        {
            return;
        }

        slot = HiddenInstances.Load(slot * 4u);
    }

    if (slot >= CountOf(Instances, GpuInstanceBytes))
    {
        return;
    }

    GpuInstance instance = LOAD(Instances, GpuInstance, slot);
    if (instance.maxScale <= 0.0)
    {
        return;
    }

    GpuModel model = LOAD(Models, GpuModel, instance.model);
    if (model.clusterCount == 0u && model.cardCount == 0u)
    {
        return;
    }

    float3 centre = InstancePosition(instance, model.bounds.xyz);
    float radius = model.bounds.w * instance.maxScale;
    if (!SphereInView(centre, radius))
    {
        return;
    }

    bool isHidden = params.phase == 1u
        ? IsHidden(mul(PrevViewProj, float4(InstancePrevPosition(instance, model.bounds.xyz), 1.0)), radius)
        : IsHidden(mul(ViewProj, float4(centre, 1.0)), radius);
    if (isHidden)
    {
        if (params.phase == 1u)
        {
            HiddenInstances.Store(WaveAllocate(CullCounts, CountHiddenInstances * 4u, 1u) * 4u, slot);
        }

        return;
    }

    float distance = length(centre);
    uint bucket = BucketOf(centre);
    float takeover = CardTakeover(model, instance, distance);
    if (takeover > 0.0)
    {
        AddCards(slot, instance, model, takeover, bucket);
    }

    if (model.clusterCount == 0u || takeover >= 1.0)
    {
        return;
    }

    // The levels whose clusters the view can want somewhere on the model: one fine enough as far off as its clusters'
    // error spheres reach, and with a parent too coarse as near as they reach, which is past its bounds. Error grows
    // level by level, so they are a run.
    float allowed = ClusterError * asfloat(Budget.Load((Buckets + bucket) * 4u));
    float perMetre = instance.maxScale * PixelScale;
    float reach = model.clusterReach * instance.maxScale;
    float nearest = max(distance - reach, Near);
    float farthest = max(distance + reach, Near);
    uint first = 0xFFFFFFFFu;
    uint last = 0u;
    [loop] for (uint level = 0u; level < model.levelCount; level++)
    {
        GpuLevel row = LOAD(Levels, GpuLevel, model.firstLevel + level);
        bool isFine = row.minError * perMetre / farthest <= allowed;
        bool isParentCoarse = row.maxParentError * perMetre / nearest > allowed;
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
    uint at = WaveAllocate(CullCounts, CountJobs * 4u, jobs);
    uint capacity = CountOf(Jobs, 16u);
    [loop] for (uint job = 0u; job < jobs && at + job < capacity; job++)
    {
        uint start = first + job * JobClusters;
        Jobs.Store4((at + job) * 16u, uint4(slot, start, min(JobClusters, last - start), bucket));
    }
}
