// The instance cull of one shadow view: a cascade of the sun or a face of a spot or point light. One group per
// page, one thread per instance slot of it, dispatched over every page (there is no page list: the group leaves at
// once when its page's cell misses the view). An instance passes when it is alive, not hidden, inside the view's
// six planes (taken from its ViewProj here, Cull/Common.hlsli FrustumPlanes, so an orthographic cascade and a
// perspective face cull the same way), inside the receivers' volume swept towards the light (Include/ShadowCull.hlsli:
// what the camera sees of this cascade's slice and everything between it and the sun; else it shadows nothing in
// view), and at least MinPixels shadow texels across. No occlusion and no late pass: a shadow map is drawn whole each
// time. A survivor is appended with the usual LOD choice, read in the shadow view's texels (LodBias is the shadow
// views' own).

#include "Include/ShadowCull.hlsli"
#include "Cull/Common.hlsli"

StructuredBuffer<GpuPage> Pages : READ(0);
StructuredBuffer<GpuCell> Cells : READ(1);
StructuredBuffer<GpuInstance> Instances : READ(2);
StructuredBuffer<GpuLodRow> Lods : READ(3);
RWStructuredBuffer<GpuDrawArgs> DrawArgs : WRITE(0);
RWStructuredBuffer<GpuVisible> VisibleIds : WRITE(1);

[numthreads(INSTANCES_PER_PAGE, 1, 1)]
void main(uint3 groupId : SV_GroupID, uint3 groupThreadId : SV_GroupThreadID)
{
    GpuPage page = Pages[groupId.x];
    if (groupThreadId.x >= page.count)
        return;

    ClipPlanes clip = FrustumPlanes();
    GpuCell cell = Cells[page.cell];
    if (page.cell != 0u && !BoxInPlanes(clip, cell.aabbMin.xyz - CameraPos, cell.aabbMax.xyz - CameraPos))
        return;

    uint slot = page.firstInstance + groupThreadId.x;
    GpuInstance instance = Instances[slot];
    if ((instance.flags & (InstanceAlive | InstanceHidden | InstanceNoShadow)) != InstanceAlive)
        return;

    float3 centerRel = instance.sphere.xyz - CameraPos;
    float radius = instance.sphere.w;
    if (!SphereInPlanes(clip, centerRel, radius) || !SphereInReceiverVolume(centerRel, radius))
        return;

    float3 center = ToView(instance.sphere.xyz);
    bool isSizeCulled = (instance.flags & InstanceNoSizeCull) == 0u && ScreenRadius(center, instance.cullRadius) < MinPixels;
    if (isSizeCulled)
        return;

    AppendVisible(DrawArgs, VisibleIds, Lods, instance.bucketGroup, slot, center, radius);
}
