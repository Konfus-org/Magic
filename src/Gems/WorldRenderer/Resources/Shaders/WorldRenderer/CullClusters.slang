// Which clusters draw this frame, decided per cluster with no walk of the hierarchy: in the frustum, facing the
// camera, at the level whose error is under a pixel, and not behind what the depth pyramid holds. The pass header's
// y picks the mode: 0 runs one group per instance over its clusters, against last frame's pyramid, keeping what only
// the pyramid hid for a retest; 1 runs the retest list against this frame's pyramid.
module "WorldRenderer/CullClusters";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Scene.Instances;
import Scene.Clusters;
import Scene.Culling;
import Scene.Visibility;

static const uint ModeInstances = 0u;
static const uint ModeRetest = 1u;

[shader("compute")]
[numthreads(64, 1, 1)]
void CullClusters(uint3 group : SV_GroupID, uint3 local : SV_GroupThreadID)
{
    FrameConstants frame = GetFrame();
    PassHeader header = GetPassHeader();
    if (header.y == ModeInstances)
    {
        uint instanceIndex = group.x;
        if (instanceIndex >= frame.instanceCount)
            return;

        GpuInstance instance = LoadInstance(frame.instances, instanceIndex);
        if (instance.clusterCount == 0u || !IsSphereInFrustum(frame, instance.boundsCenter, instance.boundsRadius))
            return;

        for (uint i = local.x; i < instance.clusterCount; i += 64u)
        {
            Test(frame, instanceIndex, instance, instance.clusterOffset + i, false);
        }
    }
    else
    {
        BufferRef counts = { frame.counts };
        uint index = group.x * RetestThreads + local.x;
        if (index >= min(counts.Load<uint>(CountRetest), frame.maxVisibleClusters))
            return;

        ArrayRef<VisibleCluster> retest = { frame.retestList };
        VisibleCluster entry = retest[index];
        GpuInstance instance = LoadInstance(frame.instances, entry.instance);
        Test(frame, entry.instance, instance, entry.cluster, true);
    }
}

// Runs the tests on one cluster and appends it to the visible list and the draws, or the retest list, as they say.
void Test(FrameConstants frame, uint instanceIndex, GpuInstance instance, uint clusterIndex, bool isRetest)
{
    GpuCluster cluster = LoadCluster(frame.clusters, clusterIndex);
    float scale = GetMaxScale(instance.world);
    float3 center = ToWorld(instance.world, cluster.center);
    float radius = cluster.radius * scale;
    if (!IsSphereInFrustum(frame, center, radius))
        return;

    // The cut: drawn where its parent group would show its error and this cluster's group would not, each error
    // projected over its group's sphere, so a cluster and its children decide the same way about the same group.
    float3 parentCenter = ToWorld(instance.world, cluster.parentCenter);
    float parentError = ProjectError(frame, parentCenter, cluster.parentRadius * scale, cluster.parentError * scale);
    float3 detailCenter = ToWorld(instance.world, cluster.detailCenter);
    float error = ProjectError(frame, detailCenter, cluster.detailRadius * scale, cluster.error * scale);
    if (!(parentError > frame.errorThreshold && error <= frame.errorThreshold))
        return;

    // The cone: culled when the camera is behind every triangle's plane, the sphere widening the cone to be sure.
    float cutoff = GetConeCutoff(cluster);
    if (cutoff < 1.0)
    {
        float3 axis = ToWorldDirection(instance.world, GetConeAxis(cluster));
        float distance = length(center);
        if (distance > radius && dot(center / distance, axis) >= cutoff + radius / distance)
            return;
    }

    BufferRef counts = { frame.counts };
    if ((frame.hasHzb != 0u || isRetest) && IsOccluded(frame, center, radius))
    {
        if (isRetest)
            return;

        uint retestIndex = counts.InterlockedAdd(CountRetest, 1u);
        if (retestIndex >= frame.maxVisibleClusters)
            return;

        ArrayRef<VisibleCluster> retest = { frame.retestList };
        VisibleCluster entry = { instanceIndex, clusterIndex };
        retest[retestIndex] = entry;
        BufferRef retestArguments = { frame.retestArguments };
        retestArguments.InterlockedMax(RetestGroups, (retestIndex / RetestThreads) + 1u);
        return;
    }

    uint listIndex = counts.InterlockedAdd(CountVisibleList, 1u);
    if (listIndex >= frame.maxVisibleClusters)
        return;

    ArrayRef<VisibleCluster> visible = { frame.visibleClusters };
    VisibleCluster entry = { instanceIndex, clusterIndex };
    visible[listIndex] = entry;

    uint drawIndex = counts.InterlockedAdd(isRetest ? CountRetestDraws : CountVisibleDraws, 1u);
    ArrayRef<DrawArguments> draws = { frame.drawArguments };
    DrawArguments arguments = { cluster.triangleCount * 3u, 1u, 0u, listIndex };
    draws[(isRetest ? frame.maxVisibleClusters : 0u) + drawIndex] = arguments;
}
