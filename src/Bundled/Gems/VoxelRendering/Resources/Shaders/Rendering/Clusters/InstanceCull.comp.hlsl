// Culls the world's instances for a view, a thread each: every slot in the first phase, those the first phase hid in
// the second. An instance in view and not hidden behind the depth pyramid (last frame's, seen through last frame's view
// as it stood then, in the first phase; this frame's in the second) hands its model's clusters to the cluster cull: only
// the levels whose error, projected from as near and as far as the model reaches, can be what the view wants there, in
// jobs of 64. Its cards go straight to the clusters drawn, from where they take over from its clusters on, the two
// dissolving into each other across a band. One the first phase hid waits for the second.

#include "Rendering/Include/Culling.hlsli"

CONSTANTS(CullConstants, c);

void AddCards(uint slot, GpuInstance instance, GpuModel model, float takeover, uint bucket)
{
    float2 dissolve = DissolveOf(instance, takeover, true);
    [loop] for (uint i = 0u; i < model.cardCount; i++)
    {
        GpuCard card = LoadCard(model.firstCard + i);
        uint row = InstanceMaterial(instance, card.slot);
        if (row == 0xFFFFu)
        {
            continue;
        }

        uint bins = LoadMaterialBins(row);
        if (!IsDrawnOpaque(bins))
        {
            continue;
        }

        uint bin = BinOf(bins, Dissolves(dissolve), true);
        uint at = WaveAllocate(BUFFER(c.counts), CountClusters * 4u, 1u);
        if (at < CountOf(BUFFER(c.clusters), 16u))
        {
            BUFFER(c.clusters).Store4(at * 16u, uint4(slot, (model.firstCard + i) | CardBit, bin, 1u));
            BUFFER(c.binCounts).InterlockedAdd(bin * 4u, 1u);
            BUFFER(c.budget).InterlockedAdd(bucket * 4u, 1u);
        }
    }
}

[numthreads(64, 1, 1)]
void main(uint3 group : SV_GroupID, uint thread : SV_GroupIndex)
{
    LoadView(c.view);
    uint slot = LinearGroup(group) * 64u + thread;
    if (c.phase == 2u)
    {
        if (slot >= BUFFER(c.counts).Load(CountHiddenInstances * 4u))
        {
            return;
        }

        slot = BUFFER(c.hiddenInstances).Load(slot * 4u);
    }

    if (slot >= CountOf(BUFFER(View.instances), 128u))
    {
        return;
    }

    GpuInstance instance = LoadInstance(slot);
    if (instance.maxScale <= 0.0)
    {
        return;
    }

    GpuModel model = LoadModel(instance.model);
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

    bool isHidden = c.phase == 1u
        ? IsHidden(c.hiZ, ToPrevClip(InstancePrevPosition(instance, model.bounds.xyz)), radius)
        : IsHidden(c.hiZ, ToClip(centre), radius);
    if (isHidden)
    {
        if (c.phase == 1u)
        {
            uint hidden = WaveAllocate(BUFFER(c.counts), CountHiddenInstances * 4u, 1u);
            BUFFER(c.hiddenInstances).Store(hidden * 4u, slot);
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
    float allowed = View.clusterError * asfloat(BUFFER(c.budget).Load((Buckets + bucket) * 4u));
    float perMetre = instance.maxScale * View.pixelScale;
    float reach = model.clusterReach * instance.maxScale;
    float nearest = max(distance - reach, View.near);
    float farthest = max(distance + reach, View.near);
    uint first = 0xFFFFFFFFu;
    uint last = 0u;
    [loop] for (uint level = 0u; level < model.levelCount; level++)
    {
        GpuLevel row = LoadLevel(model.firstLevel + level);
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
    uint at = WaveAllocate(BUFFER(c.counts), CountJobs * 4u, jobs);
    uint capacity = CountOf(BUFFER(c.jobs), 16u);
    [loop] for (uint job = 0u; job < jobs && at + job < capacity; job++)
    {
        uint start = first + job * JobClusters;
        BUFFER(c.jobs).Store4((at + job) * 16u, uint4(slot, start, min(JobClusters, last - start), bucket));
    }
}
