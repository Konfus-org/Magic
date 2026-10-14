// Turns what the shadows' culls counted (ShadowCounts) into the indirect work after them, in ShadowArgs: the cluster
// cull's groups, one a job, MaxGroupsX to a row; or the draw of the pieces, 96 indices a piece, noting whether either
// ran out of room. Neither takes more than its buffer holds. One thread.

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
    ShadowArgs.Store4(ShadowArgsPieces, uint4(PieceTriangles * 3u, pieces, 0u, 0u));
    ShadowArgs.Store(ShadowArgsPieces + 16u, 0u);
    ShadowArgs.Store(ShadowArgsOverflowed, placed > pieces || ShadowCounts.Load(ShadowCountJobs) > CountOf(ShadowJobs, 16u) ? 1u : 0u);
}
