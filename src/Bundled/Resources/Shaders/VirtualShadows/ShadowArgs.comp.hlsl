// Turns what the shadows' culls counted (ShadowCounts) into the indirect work after them, in ShadowArgs: the cluster
// cull's groups, one a job, MaxGroupsX to a row; or the draw of the pieces, 96 indices a piece. Neither takes more than
// its buffer holds. Should either cull have run out of room, the pages drawn with what it placed would be short of
// casters, and read as drawn with holes where they are missing; so instead nothing is cleared or drawn, and every page
// of the frame waits again, its rectangle back in its record, read through its coarser levels till it is drawn. The
// next frame draws half as many at once (ShadowAllocate). One thread.

#include "Include/Culling.hlsli"
#include "Include/VirtualShadows.hlsli"

struct PassParams
{
    // 0 for the cluster cull's groups, 1 for the pieces' draw.
    uint step = 0u;
};

[numthreads(1, 1, 1)]
void main()
{
    PassParams params = LoadPassParams();
    if (params.step == 0u)
    {
        uint jobs = min(ShadowCounts.Load(ShadowCountJobs), CountOf(ShadowJobs, 16u));
        ShadowArgs.Store3(ShadowArgsClusterCull, uint3(min(jobs, MaxGroupsX), (jobs + MaxGroupsX - 1u) / MaxGroupsX, 1u));
        return;
    }

    uint placed = ShadowCounts.Load(ShadowCountPieces);
    uint pieces = min(placed, CountOf(ShadowPieces, 12u));
    bool isOverflowed = placed > pieces || ShadowCounts.Load(ShadowCountJobs) > CountOf(ShadowJobs, 16u);
    ShadowArgs.Store4(ShadowArgsPieces, uint4(PieceTriangles * 3u, isOverflowed ? 0u : pieces, 0u, 0u));
    ShadowArgs.Store(ShadowArgsPieces + 16u, 0u);
    ShadowArgs.Store(ShadowArgsOverflowed, isOverflowed ? 1u : 0u);
    if (!isOverflowed)
    {
        return;
    }

    ShadowArgs.Store(ShadowArgsClear + 4u, 0u);
    GpuClipmap clipmap = ClipmapOf();
    uint views = min(ShadowCounts.Load(ShadowCountViews), CountOf(ShadowViews, 80u));
    [loop] for (uint i = 0u; i < views; i++)
    {
        GpuShadowView view = LOAD(ShadowViews, GpuShadowView, i);
        ShadowTables.Store(view.entry * 16u, view.page | PageDirty);
        uint record = RecordOf(clipmap, view.page) * 16u + 8u;
        ShadowTables.Store(record, UnionRect(ShadowTables.Load(record), view.rect));
    }
}
