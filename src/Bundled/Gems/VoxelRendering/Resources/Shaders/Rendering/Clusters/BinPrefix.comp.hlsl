// Lays a phase's pieces out by raster bin: each bin's run of the view's pieces starts where the bins before it end,
// after the first phase's when this is the second, and its draw's arguments (the phase's RasterBins of binArgs) draw
// that run, a card bin's six indices a piece, a cluster bin's 96. What does not fit the view's pieces is cut off, the
// last bins' first, for this frame: the budget draws coarser from the next. The counts are emptied for the next phase,
// the phase's end noted, and the pieces' pass told how many clusters it places. One group.

#include "Rendering/Include/Culling.hlsli"

CONSTANTS(CullConstants, c);

groupshared uint Counted[RasterBins];

[numthreads(256, 1, 1)]
void main(uint bin : SV_GroupIndex)
{
    uint count = BUFFER(c.binCounts).Load(bin * 4u);
    Counted[bin] = count;
    GroupMemoryBarrierWithGroupSync();

    uint before = 0u;
    [loop] for (uint earlier = 0u; earlier < bin; earlier++)
    {
        before += Counted[earlier];
    }

    uint phaseStart = c.phase == 1u ? 0u : BUFFER(c.counts).Load(CountPiecesFirst * 4u);
    uint capacity = CountOf(BUFFER(c.pieces), 12u);
    uint start = min(phaseStart + before, capacity);
    uint end = min(start + count, capacity);
    uint argsAt = ((c.phase - 1u) * RasterBins + bin) * 20u;
    uint indices = bin >= FixedBins + CardBinOffset ? 6u : 96u;
    BUFFER(c.binCursors).Store(bin * 4u, start);
    BUFFER(c.binArgs).Store4(argsAt, uint4(indices, end - start, 0u, 0u));
    BUFFER(c.binArgs).Store(argsAt + 16u, start);
    BUFFER(c.binCounts).Store(bin * 4u, 0u);
    if (bin != RasterBins - 1u)
    {
        return;
    }

    uint total = min(phaseStart + before + count, capacity);
    BUFFER(c.counts).Store(CountPieces * 4u, total);
    uint clustersStart = c.phase == 1u ? 0u : BUFFER(c.counts).Load(CountClustersFirst * 4u);
    uint clustersEnd = min(BUFFER(c.counts).Load(CountClusters * 4u), CountOf(BUFFER(c.clusters), 16u));
    if (c.phase == 1u)
    {
        BUFFER(c.counts).Store(CountPiecesFirst * 4u, total);
        BUFFER(c.counts).Store(CountClustersFirst * 4u, clustersEnd);
    }

    uint row = c.phase == 1u ? ArgsPieceWrite : ArgsPieceWriteSecond;
    BUFFER(c.args).Store3(row * 12u, GroupsFor(clustersEnd - min(clustersStart, clustersEnd), 64u));
}
