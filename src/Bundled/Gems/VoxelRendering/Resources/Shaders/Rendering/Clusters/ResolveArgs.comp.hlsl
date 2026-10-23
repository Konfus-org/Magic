// Writes each resolve bin's draw: a quad, six indices, over each tile the classifying pass gave it, from its run of the
// tile list, and empties its count for the next frame. One group, a thread a bin.

#include "Rendering/Include/View.hlsli"

struct ResolveArgsConstants
{
    uint view;
    uint tileCounts;
    uint tileList;
    uint resolveArgs;        // each resolve bin's draw, five uints
};

CONSTANTS(ResolveArgsConstants, c);

[numthreads(256, 1, 1)]
void main(uint bin : SV_GroupIndex)
{
    LoadView(c.view);
    uint tiles = ((uint)View.viewSize.x + 7u) / 8u * (((uint)View.viewSize.y + 7u) / 8u);
    uint capacity = CountOf(BUFFER(c.tileList), 4u);
    uint first = min(bin * tiles, capacity);
    uint count = min(BUFFER(c.tileCounts).Load(bin * 4u), capacity - first);
    BUFFER(c.resolveArgs).Store4(bin * 20u, uint4(6u, count, 0u, 0u));
    BUFFER(c.resolveArgs).Store(bin * 20u + 16u, first);
    BUFFER(c.tileCounts).Store(bin * 4u, 0u);
}
