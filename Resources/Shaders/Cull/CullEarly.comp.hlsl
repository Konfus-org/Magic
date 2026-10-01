// One group per visible page, one thread per instance slot of it: alive, in the frustum and big enough on
// screen. A survivor is appended to its draw-args bucket (AppendVisible in Cull/Common.hlsli). Dispatched
// indirectly from DispatchArgs (PageCull.comp).
//
// Compiled with OCCLUSION 1 when occlusion culling is on: an instance last frame's depth pyramid hides is
// held back instead, on the candidate list the late pass retests against this frame's pyramid. Candidates[0]
// counts them (reset by the CPU every frame), their slots follow.

#include "Cull/Common.hlsli"

#ifndef OCCLUSION
#define OCCLUSION 0
#endif

StructuredBuffer<uint> VisiblePages : READ(0);
StructuredBuffer<GpuPage> Pages : READ(1);
StructuredBuffer<GpuInstance> Instances : READ(2);
RWStructuredBuffer<GpuDrawArgs> DrawArgs : WRITE(0);
RWStructuredBuffer<uint> VisibleIds : WRITE(1);
#if OCCLUSION
StructuredBuffer<float> HiZPrevious : READ(3);
RWStructuredBuffer<uint> Candidates : WRITE(2);
#endif

[numthreads(INSTANCES_PER_PAGE, 1, 1)]
void main(uint3 groupId : SV_GroupID, uint3 groupThreadId : SV_GroupThreadID)
{
    GpuPage page = Pages[VisiblePages[groupId.x]];
    if (groupThreadId.x >= page.count)
        return;

    uint slot = page.firstInstance + groupThreadId.x;
    GpuInstance instance = Instances[slot];
    if ((instance.flags & InstanceAlive) == 0u)
        return;

    float3 center = ToView(instance.sphere.xyz);
    float radius = instance.sphere.w;
    if (!SphereInFrustum(center, radius))
        return;

    bool isSizeCulled = (instance.flags & InstanceNoSizeCull) == 0u && ScreenRadius(center, radius) < MinPixels;
    if (isSizeCulled)
        return;

#if OCCLUSION
    if (IsOccluded(HiZPrevious, center, radius))
    {
        uint place;
        InterlockedAdd(Candidates[0], 1u, place);
        Candidates[1u + place] = slot;
        return;
    }
#endif

    AppendVisible(DrawArgs, VisibleIds, instance.bucketGroup, slot);
}
