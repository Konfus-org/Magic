// Places a phase's visible clusters' pieces in the view's pieces, a thread a cluster: each takes the next run of its
// raster bin's slots and writes a piece in each, its instance, its cluster and piece (or its card), and the slot
// itself, which the visibility buffer names its triangles by. What runs past its bin's end, which the bins' pass cut
// off, waits for the next frame.

#include "Rendering/Include/Culling.hlsli"

CONSTANTS(CullConstants, c);

[numthreads(64, 1, 1)]
void main(uint3 group : SV_GroupID, uint thread : SV_GroupIndex)
{
    uint firstOfPhase = BUFFER(c.counts).Load(CountClustersFirst * 4u);
    uint first = c.phase == 1u ? 0u : firstOfPhase;
    uint end = c.phase == 1u ? firstOfPhase : min(BUFFER(c.counts).Load(CountClusters * 4u), CountOf(BUFFER(c.clusters), 16u));
    uint index = first + LinearGroup(group) * 64u + thread;
    if (index >= end)
    {
        return;
    }

    uint4 cluster = BUFFER(c.clusters).Load4(index * 16u);
    uint argsAt = ((c.phase - 1u) * RasterBins + cluster.z) * 20u;
    uint binEnd = BUFFER(c.binArgs).Load(argsAt + 16u) + BUFFER(c.binArgs).Load(argsAt + 4u);
    uint at;
    BUFFER(c.binCursors).InterlockedAdd(cluster.z * 4u, cluster.w, at);
    bool isCard = (cluster.y & CardBit) != 0u;
    [loop] for (uint piece = 0u; piece < cluster.w && at + piece < binEnd; piece++)
    {
        uint what = isCard ? cluster.y : (cluster.y << 2) | piece;
        BUFFER(c.pieces).Store3((at + piece) * 12u, uint3(cluster.x, what, at + piece));
    }
}
