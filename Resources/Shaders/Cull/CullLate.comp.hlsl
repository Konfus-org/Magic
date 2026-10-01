// The late pass: every candidate the early pass held back on last frame's pyramid is retested against this
// frame's, built from what the early pass drew. What is still hidden stays hidden; what came out from
// behind something is drawn now. Dispatched indirectly, one group per CANDIDATES_PER_GROUP candidates
// (SeedLateArgs.comp).

#include "Cull/Common.hlsli"

StructuredBuffer<uint> Candidates : READ(0); // [0] = count, then slots
StructuredBuffer<GpuInstance> Instances : READ(1);
StructuredBuffer<float> HiZ : READ(2);       // this frame's pyramid
RWStructuredBuffer<GpuDrawArgs> DrawArgs : WRITE(0); // the late args
RWStructuredBuffer<uint> VisibleIds : WRITE(1);

[numthreads(CANDIDATES_PER_GROUP, 1, 1)]
void main(uint3 threadId : SV_DispatchThreadID)
{
    if (threadId.x >= Candidates[0])
        return;

    uint slot = Candidates[1u + threadId.x];
    GpuInstance instance = Instances[slot];
    if (IsOccluded(HiZ, ToView(instance.sphere.xyz), instance.sphere.w))
        return;

    AppendVisible(DrawArgs, VisibleIds, instance.bucketGroup, slot);
}
