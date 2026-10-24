// Culls the world for a view into the pieces the visibility buffer draws. Each view culls in two phases (Karis 2021;
// Haar & Aaltonen 2015): first what last frame's depth pyramid, seen through last frame's view, says is in sight, drawn;
// then a pyramid of what was drawn, which everything the first phase hid is tested again against, so what came into
// sight is drawn the same frame, and what was wrongly hidden by a stale pyramid is too. The passes, in order:
//
//   BeginCulling   shares the view's detail out nearest the player first, and empties last frame's counts
//   CullInstances  an instance a thread: in view and not hidden, its model's levels the view can want, in jobs; its
//                  last group writes the groups the cluster cull runs, and the second phase's instance cull
//   CullClusters   a cluster a thread: fine enough and not too fine, in view and not hidden, counted into its bin; in
//                  the second phase, the clusters the first hid too; its last group lays the raster bins out
//   WritePieces    a cluster a thread: its pieces into its bin's run
//
// They share CullConstants, the renderer's Culling (WorldPasses.cs), which name the view's culling buffers: where each
// count lives in counts, the groups each indirect pass runs, the raster bins, and the distance buckets detail is shared
// out by. An indirect pass's groups are read from a buffer it does not write.

module "Renderer/Culling";

import Core.Compute;
import Renderer.DepthPyramid;
import Renderer.Pieces;

public struct CullConstants
{
    ArrayRef<uint> counts;        // the counts below
    ArrayRef<uint3> instanceArgs; // the groups of the second phase's instance cull
    ArrayRef<uint3> clusterArgs;  // the groups of each phase's cluster cull, a row a phase
    ArrayRef<uint3> pieceArgs;    // the groups of each phase's piece writing, a row a phase
    ArrayRef<uint> budget;        // the pieces each distance bucket drew, then each bucket's error scale (a float)
    ArrayRef<uint4> jobs;         // an instance's run of clusters: slot, first cluster, count, bucket
    ArrayRef<uint> hiddenInstances; // the slots the first phase hid
    ArrayRef<uint4> clusters;     // visible clusters: slot, cluster (or card with CardBit), raster bin, pieces
    ArrayRef<uint2> hiddenClusters; // the clusters the first phase hid: slot, cluster with its bucket above bit 26
    ArrayRef<uint> binCounts;     // pieces in each raster bin this phase
    ArrayRef<uint> binCursors;    // where each raster bin's next piece goes
    ArrayRef<IndexedDrawArgs> binArgs; // each raster bin's draw, a phase's RasterBins after another's
    ArrayRef<uint3> pieces;       // the view's pieces (Renderer/Pieces)
    ArrayRef<float> hiZ;          // the depth pyramid (Renderer/DepthPyramid)
    uint phase;                   // 1 for the first phase, 2 for the second
}

// Counts, a uint each.
static const uint CountJobs = 0u;           // jobs written, both phases
static const uint CountJobsFirst = 1u;      // of those, the first phase's
static const uint CountClusters = 2u;       // visible clusters written, both phases
static const uint CountClustersFirst = 3u;  // of those, the first phase's
static const uint CountHiddenInstances = 4u;
static const uint CountHiddenClusters = 5u;
static const uint CountPieces = 6u;         // pieces placed, both phases
static const uint CountPiecesFirst = 7u;    // of those, the first phase's
static const uint CountGroupsDone = 8u;     // the groups of the pass running that finished (Core/Compute's IsLastGroup)

// Raster bins: the two that do not clip, single-sided then double-sided; a surface's own clusters' from 2, and its
// cards' CardBinOffset after (the renderer's GpuMaterials.cs).
public static const uint RasterBins = 256u;
public static const uint FixedBins = 2u;
public static const uint CardBinOffset = 126u;

// A material row's bins word, as the renderer's GpuMaterials.cs writes it: its raster bin in the low byte, its resolve
// bin in the third, and these.
public static const uint BinMasked = 1u << 8;
public static const uint BinDoubleSided = 1u << 9;
public static const uint BinTransparent = 1u << 10;
public static const uint BinReady = 1u << 11;

// Clusters a culling group of 64 threads tests, one each: a job.
static const uint JobClusters = 64u;

// Buckets the distance from the player's camera is cut into, four to a doubling, which detail is shared out by nearest
// first when the pieces would not fit.
static const uint Buckets = 64u;

// The share of the view's pieces the buckets are kept within, below 1 so a sudden turn still fits.
static const float Fill = 0.85;

// The share below which a bucket drawn coarser is drawn finer again: well under Fill, so that once a bucket is fine
// enough to fit, it is not made coarse again by the detail it gets back.
static const float Refill = 0.6;

// The most a bucket's error is scaled by.
static const float MaxScale = 64.0;

groupshared uint Drawn[Buckets];
groupshared float Scales[Buckets];
groupshared uint Counted[RasterBins];

// The bucket of a camera-relative place, by its distance from the player's camera.
uint GetBucket(float3 centre)
{
    float distance = length(centre - View.priorityPosition);
    return min((uint)(log2(max(distance, 1.0)) * 4.0), Buckets - 1u);
}

// The raster bin a cluster of a material row is drawn in: its surface's own when it clips (a masked material, or a
// piece dissolving), else the single- or double-sided bin that does not; a card's, its surface's card bin.
uint GetRasterBin(uint bins, bool isDissolving, bool isCard)
{
    uint own = bins & 0xFFu;
    if (isCard)
    {
        return own + CardBinOffset;
    }

    bool clips = (bins & BinMasked) != 0u || isDissolving;
    return clips ? own : ((bins & BinDoubleSided) != 0u ? 1u : 0u);
}

// Whether a material row is in and drawn by the visibility buffer: not still loading, nor transparent.
public bool IsDrawnOpaque(uint bins)
{
    return (bins & BinReady) != 0u && (bins & BinTransparent) == 0u;
}

// Shares a view's detail out nearest the player first (budget): from how many pieces each distance bucket drew last
// frame, the buckets past where the view's pieces would not fit draw coarser this frame, their clusters' error allowed
// to cover twice as many pixels again each frame they would not fit, and a little less again each frame they fit with
// room to spare, back down to what the settings ask; in between they hold, so detail settles rather than going back and
// forth. A bucket is never finer than a nearer one. Then last frame's counts are emptied for this frame's. One group.
[shader("compute")]
[numthreads(64, 1, 1)]
void BeginCulling(uint index : SV_GroupIndex, uniform PassHeader pass, uniform CullConstants c)
{
    BeginPass(pass);
    uint capacity = c.pieces.count;
    Drawn[index] = c.budget[index];
    GroupMemoryBarrierWithGroupSync();

    // Pieces up to and including this bucket, nearest first.
    uint upTo = 0u;
    [loop] for (uint bucket = 0u; bucket <= index; bucket++)
    {
        upTo += Drawn[bucket];
    }

    // A budget never written yet reads as nothing, or as no number at all: either is the settings' own.
    float scale = asfloat(c.budget[Buckets + index]);
    scale = scale >= 1.0 && scale <= MaxScale ? scale : 1.0;
    float used = (float)upTo / (float)max(capacity, 1u);
    scale = used > Fill ? min(scale * 2.0, MaxScale) : used < Refill ? max(scale * 0.8, 1.0) : scale;
    Scales[index] = scale;
    GroupMemoryBarrierWithGroupSync();

    float coarsest = 1.0;
    [loop] for (uint nearer = 0u; nearer <= index; nearer++)
    {
        coarsest = max(coarsest, Scales[nearer]);
    }

    c.budget[Buckets + index] = asuint(coarsest);
    c.budget[index] = 0u;
    if (index <= CountPiecesFirst)
    {
        c.counts[index] = 0u;
    }
}

// Hands an instance's cards straight to the clusters drawn.
void AddCards(CullConstants c, uint slot, GpuInstance instance, GpuModel model, float takeover, uint bucket)
{
    float2 dissolve = GetDissolve(instance, takeover, true);
    [loop] for (uint i = 0u; i < model.cardCount; i++)
    {
        GpuCard card = World.cards[model.firstCard + i];
        uint row = instance.GetMaterial(card.slot);
        if (row == 0xFFFFu)
        {
            continue;
        }

        uint bins = World.materialBins[row];
        if (!IsDrawnOpaque(bins))
        {
            continue;
        }

        uint bin = GetRasterBin(bins, IsDissolving(dissolve), true);
        uint at = WaveAllocate(c.counts, CountClusters, 1u);
        if (at < c.clusters.count)
        {
            c.clusters[at] = uint4(slot, (model.firstCard + i) | CardBit, bin, 1u);
            c.binCounts.InterlockedAdd(bin, 1u);
            c.budget.InterlockedAdd(bucket, 1u);
        }
    }
}

// Culls one instance by its place among the pass's: every slot in the first phase, those the first phase hid in the
// second. An instance in view and not hidden behind the depth pyramid (last frame's, seen through last frame's view as
// it stood then, in the first phase; this frame's in the second) hands its model's clusters to the cluster cull: only
// the levels whose error, projected from as near and as far as the model reaches, can be what the view wants there, in
// jobs of 64. Its cards go straight to the clusters drawn, from where they take over from its clusters on, the two
// dissolving into each other across a band. One the first phase hid waits for the second.
void CullInstance(CullConstants c, uint place)
{
    uint slot = place;
    if (c.phase == 2u)
    {
        if (place >= c.counts[CountHiddenInstances])
        {
            return;
        }

        slot = c.hiddenInstances[place];
    }

    if (slot >= World.instances.count)
    {
        return;
    }

    GpuInstance instance = World.instances[slot];
    if (instance.maxScale <= 0.0)
    {
        return;
    }

    GpuModel model = World.models[instance.model];
    if (model.clusterCount == 0u && model.cardCount == 0u)
    {
        return;
    }

    float3 centre = instance.ToCameraRelative(model.bounds.xyz);
    float radius = model.bounds.w * instance.maxScale;
    if (!IsSphereInView(centre, radius))
    {
        return;
    }

    bool isHidden = c.phase == 1u
        ? IsHidden(c.hiZ, View.ToLastClip(instance.ToLastCameraRelative(model.bounds.xyz)), radius)
        : IsHidden(c.hiZ, View.ToClip(centre), radius);
    if (isHidden)
    {
        if (c.phase == 1u)
        {
            uint hidden = WaveAllocate(c.counts, CountHiddenInstances, 1u);
            c.hiddenInstances[hidden] = slot;
        }

        return;
    }

    float distance = length(centre);
    uint bucket = GetBucket(centre);
    float takeover = GetCardTakeover(model, instance, distance);
    if (takeover > 0.0)
    {
        AddCards(c, slot, instance, model, takeover, bucket);
    }

    if (model.clusterCount == 0u || takeover >= 1.0)
    {
        return;
    }

    // The levels whose clusters the view can want somewhere on the model: one fine enough as far off as its clusters'
    // error spheres reach, and with a parent too coarse as near as they reach, which is past its bounds. Error grows
    // level by level, so they are a run.
    float allowed = View.clusterError * asfloat(c.budget[Buckets + bucket]);
    float perMetre = instance.maxScale * View.pixelScale;
    float reach = model.clusterReach * instance.maxScale;
    float nearest = max(distance - reach, View.near);
    float farthest = max(distance + reach, View.near);
    uint first = 0xFFFFFFFFu;
    uint last = 0u;
    [loop] for (uint level = 0u; level < model.levelCount; level++)
    {
        GpuLevel row = World.levels[model.firstLevel + level];
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
    uint at = WaveAllocate(c.counts, CountJobs, jobs);
    uint capacity = c.jobs.count;
    [loop] for (uint job = 0u; job < jobs && at + job < capacity; job++)
    {
        uint start = first + job * JobClusters;
        c.jobs[at + job] = uint4(slot, start, min(JobClusters, last - start), bucket);
    }
}

// The groups the passes after the instance cull run, from the counts it kept: after the first phase's, a group a job
// of the cluster cull, the first phase's jobs noted where they end, and a thread an instance it hid for the second
// phase's instance cull; after the second phase's, a group a job it added, then a group of 64 clusters the first phase
// hid, for their retest.
void WriteCullArgs(CullConstants c)
{
    uint jobs = min(c.counts.LoadAtomic(CountJobs), c.jobs.count);
    if (c.phase == 1u)
    {
        c.counts[CountJobsFirst] = jobs;
        c.clusterArgs[0u] = GetDispatchGroups(jobs, 1u);
        c.instanceArgs[0u] = GetDispatchGroups(c.counts.LoadAtomic(CountHiddenInstances), 64u);
        return;
    }

    uint jobsSecond = jobs - min(c.counts[CountJobsFirst], jobs);
    uint hidden = min(c.counts[CountHiddenClusters], c.hiddenClusters.count);
    c.clusterArgs[1u] = GetDispatchGroups(jobsSecond + (hidden + 63u) / 64u, 1u);
}

[shader("compute")]
[numthreads(64, 1, 1)]
void CullInstances(
    uint3 group : SV_GroupID,
    uint thread : SV_GroupIndex,
    uniform PassHeader pass,
    uniform CullConstants c)
{
    BeginPass(pass);
    CullInstance(c, GetLinearGroup(group) * 64u + thread);
    if (IsLastGroup(c.counts, CountGroupsDone, thread) && thread == 0u)
    {
        WriteCullArgs(c);
    }
}

// The pixels an error of a cluster's covers from as near as its sphere comes.
float GetErrorPixels(GpuInstance instance, float4 sphere, float error)
{
    float3 centre = instance.ToCameraRelative(sphere.xyz);
    float distance = max(length(centre) - sphere.w * instance.maxScale, View.near);
    return error * instance.maxScale * View.pixelScale / distance;
}

// Culls one cluster of an instance. It is drawn where its own error, projected from its group's sphere as near as it
// comes, covers no more pixels than the view allows, and its parent's does not (Karis 2021): every cluster decides
// alone, and as a group's clusters share their sphere and error, and each parent's are never less than its children's,
// the clusters drawn meet without cracks. The pixels allowed are the settings' times the budget's for the instance's
// distance from the player. Then it must be in view and not hidden behind the depth pyramid; the first phase keeps the
// hidden for the second, which tests them again against this frame's. What is drawn is noted with its raster bin and
// pieces, counted into its bin and its distance's bucket.
void CullCluster(CullConstants c, uint slot, uint index, uint bucket)
{
    GpuInstance instance = World.instances[slot];
    GpuCluster cluster = World.clusters[index];
    uint bins = World.materialBins[instance.GetMaterial(cluster.slot)];
    if (!IsDrawnOpaque(bins))
    {
        return;
    }

    float allowed = View.clusterError * asfloat(c.budget[Buckets + bucket]);
    if (GetErrorPixels(instance, cluster.errorBounds, cluster.error) > allowed
        || GetErrorPixels(instance, cluster.parentErrorBounds, cluster.parentError) <= allowed)
    {
        return;
    }

    float3 centre = instance.ToCameraRelative(cluster.bounds.xyz);
    float radius = cluster.bounds.w * instance.maxScale;
    if (!IsSphereInView(centre, radius))
    {
        return;
    }

    bool isHidden = c.phase == 1u
        ? IsHidden(c.hiZ, View.ToLastClip(instance.ToLastCameraRelative(cluster.bounds.xyz)), radius)
        : IsHidden(c.hiZ, View.ToClip(centre), radius);
    if (isHidden)
    {
        if (c.phase == 1u)
        {
            uint hidden = WaveAllocate(c.counts, CountHiddenClusters, 1u);
            if (hidden < c.hiddenClusters.count)
            {
                c.hiddenClusters[hidden] = uint2(slot, index | (bucket << 26));
            }
        }

        return;
    }

    GpuModel model = World.models[instance.model];
    float takeover = GetCardTakeover(model, instance, length(instance.ToCameraRelative(model.bounds.xyz)));
    uint bin = GetRasterBin(bins, IsDissolving(GetDissolve(instance, takeover, false)), false);
    uint pieces = cluster.pieceCount;
    uint at = WaveAllocate(c.counts, CountClusters, 1u);
    if (at < c.clusters.count)
    {
        c.clusters[at] = uint4(slot, index, bin, pieces);
        c.binCounts.InterlockedAdd(bin, pieces);
        c.budget.InterlockedAdd(bucket, pieces);
    }
}

// Lays a phase's pieces out by raster bin, four bins a lane of the cluster cull's last group: each bin's run of the
// view's pieces starts where the bins before it end, after the first phase's when this is the second, and its draw's
// arguments (the phase's RasterBins of binArgs) draw that run, a card bin's six indices a piece, a cluster bin's 96.
// What does not fit the view's pieces is cut off, the last bins' first, for this frame: the budget draws coarser from
// the next. The counts are emptied for the next phase, the phase's end noted, and the pieces' pass told how many
// clusters it places.
void LayOutRasterBins(CullConstants c, uint lane)
{
    uint first = lane * 4u;
    [unroll] for (uint i = 0u; i < 4u; i++)
    {
        Counted[first + i] = c.binCounts.LoadAtomic(first + i);
    }

    GroupMemoryBarrierWithGroupSync();
    uint before = 0u;
    [loop] for (uint earlier = 0u; earlier < first; earlier++)
    {
        before += Counted[earlier];
    }

    uint phaseStart = c.phase == 1u ? 0u : c.counts[CountPiecesFirst];
    uint capacity = c.pieces.count;
    [unroll] for (uint i = 0u; i < 4u; i++)
    {
        uint bin = first + i;
        uint count = Counted[bin];
        uint start = min(phaseStart + before, capacity);
        uint end = min(start + count, capacity);
        IndexedDrawArgs draw;
        draw.indices = bin >= FixedBins + CardBinOffset ? 6u : 96u;
        draw.instances = end - start;
        draw.firstIndex = 0u;
        draw.vertexOffset = 0;
        draw.firstInstance = start;
        c.binCursors[bin] = start;
        c.binArgs[(c.phase - 1u) * RasterBins + bin] = draw;
        c.binCounts[bin] = 0u;
        before += count;
    }

    if (first + 4u != RasterBins)
    {
        return;
    }

    uint total = min(phaseStart + before, capacity);
    c.counts[CountPieces] = total;
    uint clustersStart = c.phase == 1u ? 0u : c.counts[CountClustersFirst];
    uint clustersEnd = min(c.counts.LoadAtomic(CountClusters), c.clusters.count);
    if (c.phase == 1u)
    {
        c.counts[CountPiecesFirst] = total;
        c.counts[CountClustersFirst] = clustersEnd;
    }

    c.pieceArgs[c.phase - 1u] = GetDispatchGroups(clustersEnd - min(clustersStart, clustersEnd), 64u);
}

// Culls clusters, a thread each: a group per job of the instance cull's, and in the second phase after them, a thread
// per cluster the first phase hid, which it tests again. Its last group lays the raster bins out.
[shader("compute")]
[numthreads(64, 1, 1)]
void CullClusters(uint3 group : SV_GroupID, uint thread : SV_GroupIndex, uniform PassHeader pass, uniform CullConstants c)
{
    BeginPass(pass);
    uint linear = GetLinearGroup(group);
    uint jobs = min(c.counts[CountJobs], c.jobs.count);
    uint firstJob = c.phase == 2u ? min(c.counts[CountJobsFirst], jobs) : 0u;
    uint jobGroups = jobs - firstJob;
    if (linear < jobGroups)
    {
        uint4 work = c.jobs[firstJob + linear];
        if (thread < work.z)
        {
            CullCluster(c, work.x, work.y + thread, work.w);
        }
    }
    else if (c.phase == 2u)
    {
        uint retest = (linear - jobGroups) * 64u + thread;
        if (retest < min(c.counts[CountHiddenClusters], c.hiddenClusters.count))
        {
            uint2 hidden = c.hiddenClusters[retest];
            CullCluster(c, hidden.x, hidden.y & 0x3FFFFFFu, hidden.y >> 26);
        }
    }

    if (IsLastGroup(c.counts, CountGroupsDone, thread))
    {
        LayOutRasterBins(c, thread);
    }
}

// Places a phase's visible clusters' pieces in the view's pieces, a thread a cluster: each takes the next run of its
// raster bin's slots and writes a piece in each, its instance, its cluster and piece (or its card), and the slot
// itself, which the visibility buffer names its triangles by. What runs past its bin's end, which the bins' layout cut
// off, waits for the next frame.
[shader("compute")]
[numthreads(64, 1, 1)]
void WritePieces(uint3 group : SV_GroupID, uint thread : SV_GroupIndex, uniform PassHeader pass, uniform CullConstants c)
{
    BeginPass(pass);
    uint firstOfPhase = c.counts[CountClustersFirst];
    uint first = c.phase == 1u ? 0u : firstOfPhase;
    uint end = c.phase == 1u ? firstOfPhase : min(c.counts[CountClusters], c.clusters.count);
    uint index = first + GetLinearGroup(group) * 64u + thread;
    if (index >= end)
    {
        return;
    }

    uint4 cluster = c.clusters[index];
    IndexedDrawArgs draw = c.binArgs[(c.phase - 1u) * RasterBins + cluster.z];
    uint binEnd = draw.firstInstance + draw.instances;
    uint at = c.binCursors.InterlockedAdd(cluster.z, cluster.w);
    bool isCard = (cluster.y & CardBit) != 0u;
    [loop] for (uint piece = 0u; piece < cluster.w && at + piece < binEnd; piece++)
    {
        uint what = isCard ? cluster.y : (cluster.y << 2) | piece;
        c.pieces[at + piece] = uint3(cluster.x, what, at + piece);
    }
}
