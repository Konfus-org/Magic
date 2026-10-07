// Prepares the late pass: its draw args start as the template with each bucket's first instance moved past
// what the early pass already put in the region, so the two passes fill disjoint parts of the visible-id
// list. Thread 0 turns the candidate count into the late dispatch's indirect arguments.

#include "Cull/Common.hlsli"

StructuredBuffer<GpuDrawArgs> Template : READ(0);
StructuredBuffer<GpuDrawArgs> EarlyArgs : READ(1);
StructuredBuffer<uint> Candidates : READ(2); // [0] = count
RWStructuredBuffer<GpuDrawArgs> LateArgs : WRITE(0);
RWStructuredBuffer<uint> DispatchArgs : WRITE(1);

[numthreads(64, 1, 1)]
void main(uint3 threadId : SV_DispatchThreadID)
{
    [branch] if (threadId.x == 0u)
    {
        DispatchArgs[0] = (Candidates[0] + CANDIDATES_PER_GROUP - 1u) / CANDIDATES_PER_GROUP;
        DispatchArgs[1] = 1u;
        DispatchArgs[2] = 1u;
    }

    uint bucketCount;
    uint stride;
    Template.GetDimensions(bucketCount, stride);
    uint bucket = threadId.x;
    if (bucket >= bucketCount)
        return;

    GpuDrawArgs args = Template[bucket];
    args.instanceCount = 0u;
    args.firstInstance += EarlyArgs[bucket].instanceCount;
    LateArgs[bucket] = args;
}
