// Which clusters draw into which of the sun's invalid pages: one group an instance, each cluster at every level
// whose texel the cluster's error fits under, into each page of that level under it that waits to be drawn. The
// pass header's x is the frame's constants and y the shadow constants.
module "WorldRenderer/ShadowCull";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Scene.Instances;
import Scene.Clusters;
import Scene.Visibility;
import Shadow.Virtual;
import WorldRenderer.ShadowPages;

[shader("compute")]
[numthreads(64, 1, 1)]
void ShadowCull(uint3 group : SV_GroupID, uint3 local : SV_GroupThreadID)
{
    FrameConstants frame = GetFrame();
    ShadowConstants shadow = LoadShadowConstants(GetPassHeader().y);
    uint instanceIndex = (group.y * CullGroupsPerRow) + group.x;
    if (instanceIndex >= frame.instanceCount)
        return;

    GpuInstance instance = LoadInstance(frame.instances, instanceIndex);
    if (instance.clusterCount == 0u)
        return;

    float scale = GetMaxScale(instance.world);
    for (uint i = local.x; i < instance.clusterCount; i += 64u)
    {
        uint clusterIndex = instance.clusterOffset + i;
        GpuCluster cluster = LoadCluster(frame.clusters, clusterIndex);
        if (IsImposter(cluster))
            continue;

        float2 light = ToLight(shadow, ToWorld(instance.world, cluster.center)).xy;
        float radius = cluster.radius * scale;
        float error = cluster.error * scale;
        float parentError = cluster.parentError * scale;
        for (uint level = 0u; level < shadow.levels; level++)
        {
            // The cut at this level: the finest clusters whose error is within a quarter of the level's texel, so
            // the receiver's bias, which grows with its slant, covers the caster's error at any slant.
            float threshold = GetTexelWorld(shadow, level) * 0.25;
            if (!(parentError > threshold && error <= threshold))
                continue;

            int2 first;
            int2 last;
            if (!GetPageRange(shadow, level, light, radius, first, last))
                continue;

            for (int y = first.y; y <= last.y; y++)
            {
                for (int x = first.x; x <= last.x; x++)
                {
                    Append(shadow, instanceIndex, clusterIndex, cluster.triangleCount, level, int2(x, y));
                }
            }
        }
    }
}

// A draw of the cluster into the page, when the page is allocated for this frame and waits to be drawn.
void Append(ShadowConstants shadow, uint instanceIndex, uint clusterIndex, uint triangles, uint level, int2 page)
{
    ArrayRef<uint2> table = { shadow.pageTable };
    uint cell = GetCell(shadow, level, page);
    uint2 entry = table[cell];
    if (!IsAllocated(entry) || entry.y != PackPage(page) || !IsInvalid(entry))
        return;

    BufferRef counts = { shadow.counts };
    uint index = counts.InterlockedAdd(ShadowCountDraws, 1u);
    if (index >= shadow.maxDraws)
        return;

    ArrayRef<ShadowDraw> draws = { shadow.draws };
    ShadowDraw draw = { instanceIndex, clusterIndex, (level << 16u) | GetLocalCell(shadow, page), GetPhysical(entry) };
    draws[index] = draw;
    ArrayRef<DrawArguments> arguments = { shadow.drawArguments };
    DrawArguments argument = { triangles * 3u, 1u, 0u, index };
    arguments[index] = argument;
}
