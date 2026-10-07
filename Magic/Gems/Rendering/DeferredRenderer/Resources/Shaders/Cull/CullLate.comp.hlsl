// The late pass: every candidate the early pass held back on last frame's pyramid is retested against this
// frame's, built from what the early pass drew. What is still hidden stays hidden; what came out from
// behind something is drawn now. Dispatched indirectly, one group per CANDIDATES_PER_GROUP candidates
// (SeedLateArgs.comp).

#include "Cull/Common.hlsli"

StructuredBuffer<uint> Candidates : READ(0); // [0] = count, then slots
StructuredBuffer<GpuInstance> Instances : READ(1);
StructuredBuffer<float> HiZ : READ(2);       // this frame's pyramid
StructuredBuffer<GpuLodRow> Lods : READ(3);
RWStructuredBuffer<GpuDrawArgs> DrawArgs : WRITE(0); // the late args
RWStructuredBuffer<GpuVisible> VisibleIds : WRITE(1);

[numthreads(CANDIDATES_PER_GROUP, 1, 1)]
void main(uint3 threadId : SV_DispatchThreadID)
{
    if (threadId.x >= Candidates[0])
        return;

    uint slot = Candidates[1u + threadId.x];
    GpuInstance instance = Instances[slot];
    float3 center = ToView(instance.sphere.xyz);
    float radius = instance.sphere.w;
    if (IsOccluded(HiZ, center, radius))
        return;

    AppendVisible(DrawArgs, VisibleIds, Lods, instance.bucketGroup, slot, center, radius);
}
