// Writes each resolve bin's draw (ResolveArgs): a quad, six indices, over each tile the classifying pass gave it, from
// its run of TileList. One group, a thread a bin.

#include "Include/Culling.hlsli"

[numthreads(256, 1, 1)]
void main(uint bin : SV_GroupIndex)
{
    uint tiles = ((uint)ViewSize.x + 7u) / 8u * (((uint)ViewSize.y + 7u) / 8u);
    uint capacity = CountOf(TileList, 4u);
    uint first = min(bin * tiles, capacity);
    uint count = min(TileCounts.Load(bin * 4u), capacity - first);
    ResolveArgs.Store4(bin * 20u, uint4(6u, count, 0u, 0u));
    ResolveArgs.Store(bin * 20u + 16u, first);
}
