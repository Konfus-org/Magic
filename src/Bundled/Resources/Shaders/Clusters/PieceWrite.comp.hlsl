// Places a phase's visible clusters' pieces in the view's pieces, a thread a cluster: each takes the next run of its
// raster bin's slots and writes a piece in each, its instance, its cluster and piece (or its card), and the slot
// itself, which the visibility buffer names its triangles by. What runs past its bin's end, which the bins' pass cut
// off, waits for the next frame.

#include "Include/Culling.hlsli"

struct PassParams
{
    // 1 for the first phase, 2 for the second.
    uint phase = 1u;
};

[numthreads(64, 1, 1)]
void main(uint3 group : SV_GroupID, uint thread : SV_GroupIndex)
{
    PassParams params = LoadPassParams();
    uint first = params.phase == 1u ? 0u : CullCounts.Load(CountClustersFirst * 4u);
    uint end = params.phase == 1u ? CullCounts.Load(CountClustersFirst * 4u) : min(CullCounts.Load(CountClusters * 4u), CountOf(VisibleClusters, 16u));
    uint index = first + LinearGroup(group) * 64u + thread;
    if (index >= end)
    {
        return;
    }

    uint4 cluster = VisibleClusters.Load4(index * 16u);
    uint argsAt = ((params.phase - 1u) * RasterBins + cluster.z) * 20u;
    uint binEnd = BinArgs.Load(argsAt + 16u) + BinArgs.Load(argsAt + 4u);
    uint at;
    BinCursors.InterlockedAdd(cluster.z * 4u, cluster.w, at);
    bool isCard = (cluster.y & CardBit) != 0u;
    [loop] for (uint piece = 0u; piece < cluster.w && at + piece < binEnd; piece++)
    {
        uint what = isCard ? cluster.y : (cluster.y << 2) | piece;
        VisiblePieces.Store3((at + piece) * 12u, uint3(cluster.x, what, at + piece));
    }
}
