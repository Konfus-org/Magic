// Turns a count the culling passes kept into the groups an indirect pass runs (a row of CullArgs): the count, less
// another when the work is what came after it, over the threads or jobs a group takes, MaxGroupsX to a row. It can note
// the count as it stands, where a phase ends. One thread.

#include "Include/Culling.hlsli"

struct PassParams
{
    // The row of CullArgs written.
    uint row = 0u;

    // The count in CullCounts the work is.
    uint counter = 0u;

    // A count it starts after, or none.
    uint since = 0xFFFFu;

    // What a group takes of it.
    uint per = 64u;

    // Where in CullCounts the count is noted as it stands, or nowhere.
    uint mark = 0xFFFFu;
};

[numthreads(1, 1, 1)]
void main()
{
    PassParams params = LoadPassParams();
    uint total = CullCounts.Load(params.counter * 4u);
    uint before = params.since != 0xFFFFu ? CullCounts.Load(params.since * 4u) : 0u;
    uint work = total - min(before, total);
    if (params.mark != 0xFFFFu)
    {
        CullCounts.Store(params.mark * 4u, total);
    }

    uint groups = (work + params.per - 1u) / max(params.per, 1u);
    CullArgs.Store3(params.row * 12u, uint3(min(groups, MaxGroupsX), (groups + MaxGroupsX - 1u) / MaxGroupsX, 1u));
}
