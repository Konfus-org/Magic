// The instance cull of every shadow view drawn again this frame, in one dispatch: a group per page per refreshed
// row (the row from the refreshed list by the group's y, which is also the slice of the draw args and the visible
// ids the view fills), one thread per instance slot of the page. An instance
// passes when it is alive, not hidden, allowed to cast, inside the view's six planes (taken from its ViewProj,
// Cull/Common.hlsli FrustumPlanes, so an orthographic cascade and a perspective face cull the same way), inside
// the receivers' volume swept towards the light (a cascade's: what the camera sees of its slice and everything
// between it and the sun; else it shadows nothing in view), and at least minTexels shadow texels across. No
// occlusion and no late pass: a shadow map is drawn whole each time. A survivor is appended into the row's slice
// of the draw args with the usual LOD choice, read in the shadow view's texels (lodBias is the shadow views' own).

#include "Include/Frame.hlsli"
#include "Include/ShadowViews.hlsli"
#include "Cull/Common.hlsli"

struct PassParams
{
    float minTexels = 1.5;
    float lodBias = 0.5;
};

StructuredBuffer<GpuPage> Pages : READ(0);
StructuredBuffer<GpuCell> Cells : READ(1);
StructuredBuffer<GpuInstance> Instances : READ(2);
StructuredBuffer<GpuLodRow> Lods : READ(3);
StructuredBuffer<GpuShadowView> ShadowViews : READ(4);
StructuredBuffer<uint> ShadowRefreshed : READ(5);
StructuredBuffer<GpuCounts> Counts : READ(6);
RWStructuredBuffer<GpuDrawArgs> ShadowDrawArgs : WRITE(0);
RWStructuredBuffer<GpuVisible> ShadowVisibleIds : WRITE(1);

// Whether a sphere, relative to the view's eye, touches the receivers' swept volume.
bool SphereInReceiverVolume(GpuShadowView view, float3 centerRel, float radius)
{
    bool inside = true;
    [loop] for (uint i = 0u; i < view.flags.z; i++)
        inside = inside && dot(view.planes[i].xyz, centerRel) + view.planes[i].w >= -radius;
    return inside;
}

[numthreads(INSTANCES_PER_PAGE, 1, 1)]
void main(uint3 groupId : SV_GroupID, uint3 groupThreadId : SV_GroupThreadID)
{
    PassParams passParams = LoadPassParams();
    uint rowIndex = ShadowRefreshed[groupId.y];
    GpuShadowView view = ShadowViews[rowIndex];
    GpuPage page = Pages[groupId.x];
    if (groupThreadId.x >= page.count)
        return;

    ClipPlanes clip = FrustumPlanesOf(view.viewProj);
    float3 eye = view.eye.xyz;
    GpuCell cell = Cells[page.cell];
    if (page.cell != 0u && !BoxInPlanes(clip, cell.aabbMin.xyz - eye, cell.aabbMax.xyz - eye))
        return;

    uint slot = page.firstInstance + groupThreadId.x;
    GpuInstance instance = Instances[slot];
    if ((instance.flags & (InstanceAlive | InstanceHidden | InstanceNoShadow)) != InstanceAlive)
        return;

    float3 centerRel = instance.sphere.xyz - eye;
    float radius = instance.sphere.w;
    if (!SphereInPlanes(clip, centerRel, radius) || !SphereInReceiverVolume(view, centerRel, radius))
        return;

    // The instance's size in the tile's texels: a cascade's texel is the same everywhere, a face's grows with the
    // distance along the face's axis.
    bool isOrthographic = (view.flags.x & ShadowViewOrthographic) != 0u;
    float along = isOrthographic ? 1.0 : max(dot(centerRel, view.rotation[2].xyz), view.range.y);
    float texels = instance.cullRadius / max(view.eye.w * along, 1e-6);
    bool isSizeCulled = (instance.flags & InstanceNoSizeCull) == 0u && texels < passParams.minTexels;
    if (isSizeCulled)
        return;

    float height = radius / max(view.eye.w * along, 1e-6) * 2.0 / view.tileTexels.w * passParams.lodBias;
    AppendVisibleHeight(ShadowDrawArgs, ShadowVisibleIds, Lods, groupId.y * Counts[0].groupCount, instance.bucketGroup, slot, height);
}
