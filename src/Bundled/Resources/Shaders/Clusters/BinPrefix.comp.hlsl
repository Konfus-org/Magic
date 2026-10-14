// Lays a phase's pieces out by raster bin: each bin's run of the view's pieces starts where the bins before it end,
// after the first phase's when this is the second, and its draw's arguments (BinArgs, the phase's 256) draw that run,
// a card bin's six indices a piece, a cluster bin's 96. What does not fit the view's pieces is cut off, the last
// bins' first, for this frame: the budget draws coarser from the next. The counts are emptied for the next phase, the
// phase's end noted, and the pieces' pass told how many clusters it places. One group.

#include "Include/Culling.hlsli"

struct PassParams
{
    // 1 for the first phase, 2 for the second.
    uint phase = 1u;
};

groupshared uint Counted[RasterBins];

[numthreads(256, 1, 1)]
void main(uint bin : SV_GroupIndex)
{
    PassParams params = LoadPassParams();
    uint count = BinCounts.Load(bin * 4u);
    Counted[bin] = count;
    GroupMemoryBarrierWithGroupSync();

    uint before = 0u;
    [loop] for (uint earlier = 0u; earlier < bin; earlier++)
    {
        before += Counted[earlier];
    }

    uint phaseStart = params.phase == 1u ? 0u : CullCounts.Load(CountPiecesFirst * 4u);
    uint capacity = CountOf(VisiblePieces, 12u);
    uint start = min(phaseStart + before, capacity);
    uint end = min(start + count, capacity);
    BinCursors.Store(bin * 4u, start);
    uint indices = bin >= FixedBins + CardBinOffset ? 6u : 96u;
    BinArgs.Store4(((params.phase - 1u) * RasterBins + bin) * 20u, uint4(indices, end - start, 0u, 0u));
    BinArgs.Store(((params.phase - 1u) * RasterBins + bin) * 20u + 16u, start);
    BinCounts.Store(bin * 4u, 0u);
    if (bin != RasterBins - 1u)
    {
        return;
    }

    uint total = min(phaseStart + before + count, capacity);
    CullCounts.Store(CountPieces * 4u, total);
    uint clustersStart = params.phase == 1u ? 0u : CullCounts.Load(CountClustersFirst * 4u);
    uint clustersEnd = min(CullCounts.Load(CountClusters * 4u), CountOf(VisibleClusters, 16u));
    if (params.phase == 1u)
    {
        CullCounts.Store(CountPiecesFirst * 4u, total);
        CullCounts.Store(CountClustersFirst * 4u, clustersEnd);
    }

    uint groups = (clustersEnd - min(clustersStart, clustersEnd) + 63u) / 64u;
    CullArgs.Store3((params.phase == 1u ? ArgsPieceWrite : ArgsPieceWriteSecond) * 12u, uint3(min(groups, MaxGroupsX), (groups + MaxGroupsX - 1u) / MaxGroupsX, 1u));
}
