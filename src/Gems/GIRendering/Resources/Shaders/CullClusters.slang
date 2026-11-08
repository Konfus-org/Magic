// Which clusters draw this frame, decided per cluster with no walk of the hierarchy: of an instance in the frustum
// and at least five pixels across, those in the frustum, facing the camera, at the level whose error is under the
// frame's threshold, and not behind what the depth pyramid holds. Every cluster that passes, shown now or hidden by
// last frame's pyramid, holds a row of the visible list, and the nearest instances get theirs first: one pass counts
// each instance's rows into a bin by its distance, one thread finds how many bins from the nearest the list has room
// for, and a last pass gives those instances their rows, all together, and draws the rest as their impostors whole.
// So the list never overflows, no instance is drawn in part, and only the farthest go without their clusters. The
// pass header's y picks the pass: ModeCount, ModeFit and ModePlace run against last frame's pyramid, keeping what only
// the pyramid hid for a retest, and ModeRetest runs the retest list against this frame's. Its z is the impostor draws
// and its w the rows each instance asks for.
module "CullClusters";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Scene.Instances;
import Scene.Clusters;
import Scene.Materials;
import Scene.Culling;
import Scene.Visibility;
import Scene.Impostors;

static const uint ModeCount = 0u;
static const uint ModeFit = 1u;
static const uint ModePlace = 2u;
static const uint ModeRetest = 3u;

// What a cluster's tests say: not drawn, drawn, or hidden only by last frame's depth pyramid, to retest.
enum Outcome
{
    Culled,
    Shown,
    Hidden,
}

// A retest list entry: a cluster's row of the visible list and its cluster, or an instance and RetestImpostor or
// RetestWholeImpostor in place of the cluster, for its impostor.
struct RetestEntry
{
    uint index;
    uint cluster;
}

// The instance's group's count of its clusters that pass, the first of the rows reserved for them, and how many of
// those are handed out.
groupshared uint passedCount;
groupshared uint firstRow;
groupshared uint takenRows;

[shader("compute")]
[numthreads(64, 1, 1)]
void CullClusters(uint3 group : SV_GroupID, uint3 local : SV_GroupThreadID)
{
    FrameConstants frame = GetFrame();
    PassHeader header = GetPassHeader();
    if (header.y == ModeFit)
    {
        if (local.x == 0u)
            Fit(frame);

        return;
    }

    if (header.y == ModeRetest)
    {
        Retest(frame, header.z, group.x * RetestThreads + local.x);
        return;
    }

    uint instanceIndex = (group.y * CullGroupsPerRow) + group.x;
    if (instanceIndex >= frame.instanceCount)
        return;

    // An instance less than five pixels across on screen draws nothing: neither clusters nor impostor.
    GpuInstance instance = LoadInstance(frame.instances, instanceIndex);
    float3 center = Relative(frame, instance.boundsCenter);
    bool isInView =
        instance.clusterCount != 0u
        && IsSphereInFrustum(frame, center, instance.boundsRadius)
        && GetPixelRadius(frame, center, instance.boundsRadius) >= MinPixelRadius;
    if (!isInView)
        return;

    // Past the handoff's end the instance draws as its impostor alone.
    float fade = GetImpostorFade(frame, instance);
    if (fade >= 1.0)
    {
        if (header.y == ModePlace && local.x == 0u)
            TestImpostor(frame, header.z, instanceIndex, instance, false, false);

        return;
    }

    ArrayRef<uint> instanceRows = { header.w };
    uint bin = GetDemandBin(max(length(center) - instance.boundsRadius, 0.0));
    if (header.y == ModeCount)
        Count(frame, instanceRows, instanceIndex, instance, bin, local.x);
    else
        Place(frame, header.z, instanceRows, instanceIndex, instance, bin, fade, local.x);
}

// Counts the instance's clusters that pass, keeps the count as its rows, and adds them to its distance bin.
void Count(FrameConstants frame, ArrayRef<uint> instanceRows, uint instanceIndex, GpuInstance instance, uint bin, uint thread)
{
    if (thread == 0u)
        passedCount = 0u;

    GroupMemoryBarrierWithGroupSync();
    for (uint i = thread; i < instance.clusterCount; i += 64u)
    {
        if (Test(frame, instance, instance.clusterOffset + i, false) != Outcome.Culled)
            InterlockedAdd(passedCount, 1u);
    }

    GroupMemoryBarrierWithGroupSync();
    if (thread != 0u)
        return;

    instanceRows[instanceIndex] = passedCount;
    BufferRef counts = { frame.counts };
    counts.InterlockedAdd(DemandBinsOffset + bin * 4u, passedCount);
}

// From the nearest bin out, the first whose rows no longer fit the list after the bins before it; and every row asked
// for, which the cluster budget reads.
void Fit(FrameConstants frame)
{
    BufferRef counts = { frame.counts };
    uint wanted = 0u;
    uint fitBins = DemandBins;
    for (uint bin = 0u; bin < DemandBins; bin++)
    {
        uint rows = counts.Load<uint>(DemandBinsOffset + bin * 4u);
        if (fitBins == DemandBins && wanted + rows > frame.maxVisibleClusters)
            fitBins = bin;

        wanted += rows;
    }

    counts.Store<uint>(CountWantedRows, wanted);
    counts.Store<uint>(CountFitBins, fitBins);
}

// Gives an instance in a bin with room the rows it counted, in one go, and puts its clusters in them; one past them
// draws its impostor whole, and one in the handoff its impostor's share of the dither too.
void Place(
    FrameConstants frame,
    uint impostorDraws,
    ArrayRef<uint> instanceRows,
    uint instanceIndex,
    GpuInstance instance,
    uint bin,
    float fade,
    uint thread
)
{
    BufferRef counts = { frame.counts };
    bool fits = bin < counts.Load<uint>(CountFitBins);
    if (thread == 0u && (fade > 0.0 || !fits) && instance.impostor != 0u)
        TestImpostor(frame, impostorDraws, instanceIndex, instance, !fits, false);

    uint rows = instanceRows[instanceIndex];
    if (!fits || rows == 0u)
        return;

    if (thread == 0u)
    {
        firstRow = counts.InterlockedAdd(CountVisibleList, rows);
        takenRows = 0u;
    }

    GroupMemoryBarrierWithGroupSync();
    bool isFading = fade > 0.0;
    for (uint i = thread; i < instance.clusterCount; i += 64u)
    {
        uint clusterIndex = instance.clusterOffset + i;
        Outcome outcome = Test(frame, instance, clusterIndex, false);
        if (outcome == Outcome.Culled)
            continue;

        uint taken = 0u;
        InterlockedAdd(takenRows, 1u, taken);
        uint row = firstRow + taken;
        ArrayRef<VisibleCluster> visible = { frame.visibleClusters };
        VisibleCluster entry = { instanceIndex, clusterIndex };
        visible[row] = entry;
        if (outcome == Outcome.Shown || !TryAddRetest(frame, row, clusterIndex))
            AddDraw(frame, instance, clusterIndex, row, isFading, false);
    }
}

// The retest list's entry at index, against this frame's pyramid: a cluster draws from the row it holds, an impostor
// as its first test would have.
void Retest(FrameConstants frame, uint impostorDraws, uint index)
{
    BufferRef counts = { frame.counts };
    if (index >= min(counts.Load<uint>(CountRetest), frame.maxVisibleClusters))
        return;

    ArrayRef<RetestEntry> retest = { frame.retestList };
    RetestEntry entry = retest[index];
    if (entry.cluster == RetestImpostor || entry.cluster == RetestWholeImpostor)
    {
        GpuInstance instance = LoadInstance(frame.instances, entry.index);
        TestImpostor(frame, impostorDraws, entry.index, instance, entry.cluster == RetestWholeImpostor, true);
        return;
    }

    ArrayRef<VisibleCluster> visible = { frame.visibleClusters };
    VisibleCluster cluster = visible[entry.index];
    GpuInstance instance = LoadInstance(frame.instances, cluster.instance);
    if (Test(frame, instance, cluster.cluster, true) == Outcome.Shown)
        AddDraw(frame, instance, cluster.cluster, entry.index, GetImpostorFade(frame, instance) > 0.0, true);
}

// The tests on one cluster: the frustum, the cut, the cone and the depth pyramid, last frame's on the first pass and
// this frame's on the retest.
Outcome Test(FrameConstants frame, GpuInstance instance, uint clusterIndex, bool isRetest)
{
    GpuCluster cluster = LoadCluster(frame.clusters, clusterIndex);
    float scale = GetMaxScale(instance.world);
    float3 center = Relative(frame, ToWorld(instance.world, cluster.center));
    float radius = cluster.radius * scale;
    if (!IsSphereInFrustum(frame, center, radius))
        return Outcome.Culled;

    // The cut: drawn where its parent group would show its error and this cluster's group would not, each error
    // projected over its group's sphere, so a cluster and its children decide the same way about the same group.
    float3 parentCenter = Relative(frame, ToWorld(instance.world, cluster.parentCenter));
    float parentError = ProjectError(frame, parentCenter, cluster.parentRadius * scale, cluster.parentError * scale);
    float3 detailCenter = Relative(frame, ToWorld(instance.world, cluster.detailCenter));
    float error = ProjectError(frame, detailCenter, cluster.detailRadius * scale, cluster.error * scale);
    if (!(parentError > frame.errorThreshold && error <= frame.errorThreshold))
        return Outcome.Culled;

    // The cone: culled when the camera is behind every triangle's plane, the sphere widening the cone to be sure.
    float cutoff = GetConeCutoff(cluster);
    if (cutoff < 1.0)
    {
        float3 axis = ToWorldDirection(instance.world, GetConeAxis(cluster));
        float distance = length(center);
        if (distance > radius && dot(center / distance, axis) >= cutoff + radius / distance)
            return Outcome.Culled;
    }

    if ((frame.hasHzb != 0u || isRetest) && IsOccluded(frame, center, radius))
        return isRetest ? Outcome.Culled : Outcome.Hidden;

    return Outcome.Shown;
}

// Puts the cluster at row on the retest list, or says the list is full, when it draws now untested instead.
bool TryAddRetest(FrameConstants frame, uint row, uint clusterIndex)
{
    BufferRef counts = { frame.counts };
    uint retestIndex = counts.InterlockedAdd(CountRetest, 1u);
    if (retestIndex >= frame.maxVisibleClusters)
        return false;

    ArrayRef<RetestEntry> retest = { frame.retestList };
    RetestEntry entry = { row, clusterIndex };
    retest[retestIndex] = entry;
    BufferRef retestArguments = { frame.retestArguments };
    retestArguments.InterlockedMax(RetestGroups, (retestIndex / RetestThreads) + 1u);
    return true;
}

// Appends the draw of the cluster at row to its region of the draws: by pass, and by whether its instance is in the
// impostor handoff, which draws with the dither; a translucent cluster to the glass's, from either pass. Each row draws
// once, so no region outgrows the visible list.
void AddDraw(FrameConstants frame, GpuInstance instance, uint clusterIndex, uint row, bool isFading, bool isRetest)
{
    static const uint countOffsets[5] = {
        CountVisibleDraws, CountRetestDraws, CountFadingDraws, CountRetestFadingDraws, CountGlassDraws
    };
    GpuCluster cluster = LoadCluster(frame.clusters, clusterIndex);
    bool isGlass = IsTranslucent(frame.materials, GetMaterial(instance, cluster.materialSlot));
    uint region = isGlass ? GlassRegion : (isRetest ? 1u : 0u) + (isFading ? 2u : 0u);
    BufferRef counts = { frame.counts };
    uint drawIndex = counts.InterlockedAdd(countOffsets[region], 1u);
    ArrayRef<DrawArguments> draws = { frame.drawArguments };
    DrawArguments arguments = { cluster.triangleCount * 3u, 1u, 0u, row };
    draws[region * frame.maxVisibleClusters + drawIndex] = arguments;
}

// Appends the instance's impostor to the draws, the retested ones after the first's, whole or with its share of the
// handoff's dither, or, when the old depth pyramid hides it, to the retest list; with no room there it draws now.
void TestImpostor(FrameConstants frame, uint draws, uint instanceIndex, GpuInstance instance, bool isWhole, bool isRetest)
{
    BufferRef counts = { frame.counts };
    if ((frame.hasHzb != 0u || isRetest) && IsOccluded(frame, Relative(frame, instance.boundsCenter), instance.boundsRadius))
    {
        if (isRetest)
            return;

        uint retestIndex = counts.InterlockedAdd(CountRetest, 1u);
        if (retestIndex < frame.maxVisibleClusters)
        {
            ArrayRef<RetestEntry> retest = { frame.retestList };
            RetestEntry entry = { instanceIndex, isWhole ? RetestWholeImpostor : RetestImpostor };
            retest[retestIndex] = entry;
            BufferRef retestArguments = { frame.retestArguments };
            retestArguments.InterlockedMax(RetestGroups, (retestIndex / RetestThreads) + 1u);
            return;
        }
    }

    uint drawIndex = counts.InterlockedAdd(isRetest ? CountRetestImpostorDraws : CountImpostorDraws, 1u);
    if (drawIndex >= frame.maxVisibleClusters)
        return;

    ArrayRef<DrawArguments> impostorDraws = { draws };
    DrawArguments arguments = { 6u, 1u, 0u, instanceIndex | (isWhole ? ImpostorWhole : 0u) };
    impostorDraws[(isRetest ? frame.maxVisibleClusters : 0u) + drawIndex] = arguments;
}
