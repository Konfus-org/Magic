// Which clusters draw this frame, decided per cluster with no walk of the hierarchy: of an instance in the frustum
// and at least five pixels across, those in the frustum, facing the camera, at the level whose error is under the
// frame's threshold, and not behind what the depth pyramid holds. Every cluster that passes, shown now or hidden by
// last frame's pyramid, holds a row of the visible list, and the nearest instances get theirs first: CountClusters
// counts each instance's rows into a bin by its distance, FitClusters finds how many bins from the nearest the list
// has room for, and PlaceClusters gives those instances their rows, all together, and draws the rest as their
// impostors whole. So the list never overflows, no instance is drawn in part, and only the farthest go without their
// clusters. Those three run against last frame's pyramid, keeping what only the pyramid hid for a retest, and
// RetestClusters runs the retest list against this frame's.
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
import Scene.Models;

// The instance cull's groups go along x up to this many and then down y, as a GPU takes no more along one axis.
static const uint CullGroupsPerRow = 65535u;

// The threads of a retest group, and the word of the retest arguments that holds the groups, before 1 and 1.
static const uint RetestThreads = 64u;
static const uint RetestGroups = 0u;

// The counts buffer's words the cull alone reads, as byte offsets: the retest list's length, the visible list's, and
// the first distance bin with no room left: instances in it and past it draw as their impostors.
static const uint CountRetest = 28u;
static const uint CountVisibleList = 32u;
static const uint CountFitBins = 40u;

// The counts buffer's distance bins from DemandBinsOffset, a word each: the rows asked for by the instances whose
// nearest point is that far from the camera, four bins to each doubling of the distance in metres.
static const uint DemandBinsOffset = 64u;
static const uint DemandBins = 64u;

// One draw's arguments, as the GPU reads them: a cluster's triangles three vertices each, its visible row the first
// instance. The draw is not indexed, so a vertex's id is its place in the cluster and names its triangle, which a
// pixel shader cannot ask for on every GPU.
struct DrawArguments
{
    uint vertexCount;
    uint instanceCount;
    uint firstVertex;
    uint firstInstance;
}

// The draw arguments' regions, C#'s DrawRegion, each as many draws as the visible list has rows and counted at word
// region * 4 of the counts buffer: the clusters the old depth pyramid shows, then those the retest finds the new one
// shows; the same two of the clusters in the impostor handoff, which draw with the dither; the translucent clusters
// from both; and the impostors of both.
enum DrawRegion
{
    Visible,
    Retested,
    Fading,
    RetestedFading,
    Glass,
    Impostors,
    RetestedImpostors,
}

// What a cluster's tests say: not drawn, drawn, or hidden only by last frame's depth pyramid, to retest.
enum ClusterOutcome
{
    Culled,
    Shown,
    Hidden,
}

// A retest list entry: a cluster's row of the visible list and its cluster, or an impostor draw's first instance and
// RetestImpostor in place of the cluster.
struct RetestEntry
{
    uint index;
    uint cluster;
}

static const uint RetestImpostor = 0xFFFFFFFFu;

// The instance's group's count of its clusters that pass, the first of the rows reserved for them, and how many of
// those are handed out.
groupshared uint passedCount;
groupshared uint firstRow;
groupshared uint takenRows;

// Counts the instance's clusters that pass, keeps the count as its rows, and adds them to its distance bin.
[shader("compute")]
[numthreads(64, 1, 1)]
void CountClusters(uint3 group : SV_GroupID, uint3 local : SV_GroupThreadID)
{
    Models models = GetModels();
    uint instanceIndex = (group.y * CullGroupsPerRow) + group.x;
    if (instanceIndex >= models.count)
        return;

    FrameConstants frame = GetFrame();
    GpuInstance instance = LoadInstance(models.instances, instanceIndex);
    if (!IsInView(frame, instance) || GetImpostorFade(frame, instance) >= 1.0)
        return;

    if (local.x == 0u)
        passedCount = 0u;

    GroupMemoryBarrierWithGroupSync();
    GpuClusterModel model = LoadClusterModel(instance.model);
    for (uint i = local.x; i < model.clusterCount; i += 64u)
    {
        if (GetClusterOutcome(frame, instance, model.clusterOffset + i) != ClusterOutcome.Culled)
            InterlockedAdd(passedCount, 1u);
    }

    GroupMemoryBarrierWithGroupSync();
    if (local.x != 0u)
        return;

    ArrayRef<uint> instanceRows = { GetClusters().instanceRows };
    instanceRows[instanceIndex] = passedCount;
    BufferRef counts = { GetClusters().counts };
    counts.InterlockedAdd(DemandBinsOffset + GetDemandBin(frame, instance) * 4u, passedCount);
}

// From the nearest bin out, the first whose rows no longer fit the list after the bins before it; and every row asked
// for, which the cluster budget reads.
[shader("compute")]
[numthreads(1, 1, 1)]
void FitClusters()
{
    ClusterConstants clusters = GetClusters();
    BufferRef counts = { clusters.counts };
    uint wanted = 0u;
    uint fitBins = DemandBins;
    for (uint bin = 0u; bin < DemandBins; bin++)
    {
        uint rows = counts.Load<uint>(DemandBinsOffset + bin * 4u);
        if (fitBins == DemandBins && wanted + rows > clusters.maxVisibleClusters)
            fitBins = bin;

        wanted += rows;
    }

    counts.Store<uint>(CountWantedRows, wanted);
    counts.Store<uint>(CountFitBins, fitBins);
}

// Gives an instance in a bin with room the rows it counted, in one go, and puts its clusters in them; one past them
// draws its impostor whole, and one in the handoff its impostor's share of the dither too.
[shader("compute")]
[numthreads(64, 1, 1)]
void PlaceClusters(uint3 group : SV_GroupID, uint3 local : SV_GroupThreadID)
{
    Models models = GetModels();
    uint instanceIndex = (group.y * CullGroupsPerRow) + group.x;
    if (instanceIndex >= models.count)
        return;

    FrameConstants frame = GetFrame();
    GpuInstance instance = LoadInstance(models.instances, instanceIndex);
    if (!IsInView(frame, instance))
        return;

    // Past the handoff's end the instance draws as its impostor alone.
    float fade = GetImpostorFade(frame, instance);
    if (fade >= 1.0)
    {
        if (local.x == 0u)
            TestImpostor(frame, instance, instanceIndex, DrawRegion.Impostors);

        return;
    }

    ClusterConstants clusters = GetClusters();
    BufferRef counts = { clusters.counts };
    bool fits = GetDemandBin(frame, instance) < counts.Load<uint>(CountFitBins);
    GpuClusterModel model = LoadClusterModel(instance.model);
    if (local.x == 0u && (fade > 0.0 || !fits) && model.impostor != 0u)
        TestImpostor(frame, instance, instanceIndex | (fits ? 0u : ImpostorWhole), DrawRegion.Impostors);

    ArrayRef<uint> instanceRows = { clusters.instanceRows };
    uint rows = instanceRows[instanceIndex];
    if (!fits || rows == 0u)
        return;

    if (local.x == 0u)
    {
        firstRow = counts.InterlockedAdd(CountVisibleList, rows);
        takenRows = 0u;
    }

    GroupMemoryBarrierWithGroupSync();
    DrawRegion region = fade > 0.0 ? DrawRegion.Fading : DrawRegion.Visible;
    for (uint i = local.x; i < model.clusterCount; i += 64u)
    {
        uint clusterIndex = model.clusterOffset + i;
        ClusterOutcome outcome = GetClusterOutcome(frame, instance, clusterIndex);
        if (outcome == ClusterOutcome.Culled)
            continue;

        uint taken = 0u;
        InterlockedAdd(takenRows, 1u, taken);
        uint row = firstRow + taken;
        ArrayRef<VisibleCluster> visible = { clusters.visibleClusters };
        VisibleCluster entry = { instanceIndex, clusterIndex };
        visible[row] = entry;
        if (outcome == ClusterOutcome.Shown || !TryAddRetest(row, clusterIndex))
            AddClusterDraw(instance, clusterIndex, row, region);
    }
}

// The retest list's entry at the thread's index, against this frame's pyramid: a cluster draws from the row it holds,
// an impostor as its first test would have.
[shader("compute")]
[numthreads(RetestThreads, 1, 1)]
void RetestClusters(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    Models models = GetModels();
    ClusterConstants clusters = GetClusters();
    BufferRef counts = { clusters.counts };
    if (thread.x >= min(counts.Load<uint>(CountRetest), clusters.maxVisibleClusters))
        return;

    ArrayRef<RetestEntry> retest = { clusters.retestList };
    RetestEntry entry = retest[thread.x];
    if (entry.cluster == RetestImpostor)
    {
        GpuInstance instance = LoadInstance(models.instances, entry.index & ~ImpostorWhole);
        TestImpostor(frame, instance, entry.index, DrawRegion.RetestedImpostors);
        return;
    }

    ArrayRef<VisibleCluster> visible = { clusters.visibleClusters };
    VisibleCluster cluster = visible[entry.index];
    GpuInstance instance = LoadInstance(models.instances, cluster.instance);
    if (GetClusterOutcome(frame, instance, cluster.cluster) != ClusterOutcome.Shown)
        return;

    bool isFading = GetImpostorFade(frame, instance) > 0.0;
    AddClusterDraw(instance, cluster.cluster, entry.index, isFading ? DrawRegion.RetestedFading : DrawRegion.Retested);
}

// Whether the instance is in the frustum and at least five pixels across on screen; one smaller draws nothing,
// neither clusters nor impostor.
bool IsInView(FrameConstants frame, GpuInstance instance)
{
    float3 center = Relative(frame, instance.boundsCenter);
    return instance.model != 0u
        && IsSphereInFrustum(frame, center, instance.boundsRadius)
        && GetPixelRadius(frame, center, instance.boundsRadius) >= MinPixelRadius;
}

// The distance bin of the instance's nearest point.
uint GetDemandBin(FrameConstants frame, GpuInstance instance)
{
    float distance = length(Relative(frame, instance.boundsCenter)) - instance.boundsRadius;
    return min(uint(log2(max(distance, 1.0)) * 4.0), DemandBins - 1u);
}

// The tests on one cluster: the frustum, the cut, the cone and the depth pyramid, last frame's on the first pass and
// this frame's on the retest. The retest list holds entries only when there was a pyramid to test against.
ClusterOutcome GetClusterOutcome(FrameConstants frame, GpuInstance instance, uint clusterIndex)
{
    ClusterConstants clusters = GetClusters();
    GpuCluster cluster = LoadCluster(clusters.clusters, clusterIndex);
    float scale = GetMaxScale(instance.world);
    float3 center = Relative(frame, ToWorld(instance.world, cluster.center));
    float radius = cluster.radius * scale;
    if (!IsSphereInFrustum(frame, center, radius))
        return ClusterOutcome.Culled;

    // The cut: drawn where its parent group would show its error and this cluster's group would not, each error
    // projected over its group's sphere, so a cluster and its children decide the same way about the same group.
    float3 parentCenter = Relative(frame, ToWorld(instance.world, cluster.parentCenter));
    float parentError = ProjectError(frame, parentCenter, cluster.parentRadius * scale, cluster.parentError * scale);
    float3 detailCenter = Relative(frame, ToWorld(instance.world, cluster.detailCenter));
    float error = ProjectError(frame, detailCenter, cluster.detailRadius * scale, cluster.error * scale);
    if (!(parentError > clusters.errorThreshold && error <= clusters.errorThreshold))
        return ClusterOutcome.Culled;

    // The cone: culled when the camera is behind every triangle's plane, the sphere widening the cone to be sure.
    float cutoff = GetConeCutoff(cluster);
    if (cutoff < 1.0)
    {
        float3 axis = ToWorldDirection(instance.world, GetConeAxis(cluster));
        float distance = length(center);
        if (distance > radius && dot(center / distance, axis) >= cutoff + radius / distance)
            return ClusterOutcome.Culled;
    }

    if (clusters.hasDepthPyramid != 0u && IsOccluded(frame, center, radius))
        return ClusterOutcome.Hidden;

    return ClusterOutcome.Shown;
}

// Puts index and cluster, a cluster's row and its cluster or an impostor draw's first instance and RetestImpostor, on
// the retest list, or says the list is full, when it draws now untested instead.
bool TryAddRetest(uint index, uint cluster)
{
    ClusterConstants clusters = GetClusters();
    BufferRef counts = { clusters.counts };
    uint retestIndex = counts.InterlockedAdd(CountRetest, 1u);
    if (retestIndex >= clusters.maxVisibleClusters)
        return false;

    ArrayRef<RetestEntry> retest = { clusters.retestList };
    RetestEntry entry = { index, cluster };
    retest[retestIndex] = entry;
    BufferRef retestArguments = { clusters.retestArguments };
    retestArguments.InterlockedMax(RetestGroups, (retestIndex / RetestThreads) + 1u);
    return true;
}

// Appends the draw of the cluster at row to region, its pass's region of the draws with or without the handoff's
// dither; a translucent cluster to the glass's, from either pass.
void AddClusterDraw(GpuInstance instance, uint clusterIndex, uint row, DrawRegion region)
{
    GpuCluster cluster = LoadCluster(GetClusters().clusters, clusterIndex);
    bool isGlass = IsTranslucent(GetModels().materials, GetMaterial(instance, cluster.materialSlot));
    DrawArguments arguments = { cluster.triangleCount * 3u, 1u, 0u, row };
    AddDraw(isGlass ? DrawRegion.Glass : region, arguments);
}

// Appends the instance's impostor to region, the impostors of its pass, with firstInstance its draw's first instance,
// which says whether it draws whole; or, when the old depth pyramid hides it, to the retest list, and with no room
// there it draws now. On the retest, a hidden impostor draws nothing.
void TestImpostor(FrameConstants frame, GpuInstance instance, uint firstInstance, DrawRegion region)
{
    ClusterConstants clusters = GetClusters();
    bool isHidden = clusters.hasDepthPyramid != 0u && IsOccluded(frame, Relative(frame, instance.boundsCenter), instance.boundsRadius);
    if (isHidden && (region == DrawRegion.RetestedImpostors || TryAddRetest(firstInstance, RetestImpostor)))
        return;

    DrawArguments arguments = { 6u, 1u, 0u, firstInstance };
    AddDraw(region, arguments);
}

// Appends a draw to region of the draws, counted at the region's word of the counts. A cluster's row draws once, so
// no region of clusters outgrows the visible list; past it an impostor goes undrawn.
void AddDraw(DrawRegion region, DrawArguments arguments)
{
    ClusterConstants clusters = GetClusters();
    BufferRef counts = { clusters.counts };
    uint drawIndex = counts.InterlockedAdd(uint(region) * 4u, 1u);
    if (drawIndex >= clusters.maxVisibleClusters)
        return;

    ArrayRef<DrawArguments> draws = { clusters.drawArguments };
    draws[uint(region) * clusters.maxVisibleClusters + drawIndex] = arguments;
}
