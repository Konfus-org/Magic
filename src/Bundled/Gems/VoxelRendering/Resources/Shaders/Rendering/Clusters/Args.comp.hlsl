// Turns the counts the instance cull kept into the groups the passes after it run: after the first phase's, a group a
// job of the cluster cull, the first phase's jobs noted where they end, and a thread an instance it hid for the second
// phase's instance cull; after the second phase's, a group a job it added, and a thread a cluster the first phase hid
// for their retest. One thread.

#include "Rendering/Include/Culling.hlsli"

CONSTANTS(CullConstants, c);

[numthreads(1, 1, 1)]
void main()
{
    uint jobs = min(BUFFER(c.counts).Load(CountJobs * 4u), CountOf(BUFFER(c.jobs), 16u));
    if (c.phase == 1u)
    {
        BUFFER(c.counts).Store(CountJobsFirst * 4u, jobs);
        BUFFER(c.args).Store3(ArgsClusterCull * 12u, GroupsFor(jobs, 1u));
        BUFFER(c.args).Store3(ArgsInstanceCullSecond * 12u, GroupsFor(BUFFER(c.counts).Load(CountHiddenInstances * 4u), 64u));
        return;
    }

    uint first = BUFFER(c.counts).Load(CountJobsFirst * 4u);
    uint hidden = min(BUFFER(c.counts).Load(CountHiddenClusters * 4u), CountOf(BUFFER(c.hiddenClusters), 8u));
    BUFFER(c.args).Store3(ArgsClusterCullSecond * 12u, GroupsFor(jobs - min(first, jobs), 1u));
    BUFFER(c.args).Store3(ArgsClusterRetest * 12u, GroupsFor(hidden, 64u));
}
