// The constants of a shadow view's cull (Cull/CullShadow.comp.hlsl): the view's frame block, then the planes
// of the receivers' volume swept towards the light, as ShadowCullConstants in Gems/DeferredRenderer/GpuStructs.cs
// lays them out. A caster that is outside the swept volume shadows nothing the camera sees, however well it
// fits the shadow view, and a sun cascade's volume is wide: without this every instance for hundreds of
// metres around would be drawn into it. The planes are in the shadow view's own space (positions relative to
// its CameraPos), normals pointing in, and there are none for a local light's face.

#ifndef MAGIC_SHADOW_CULL_HLSLI
#define MAGIC_SHADOW_CULL_HLSLI

#define FRAME_APPEND \
    float4 ReceiverPlanes[12]; \
    uint ReceiverPlaneCount; \
    uint ReceiverPad0; \
    uint ReceiverPad1; \
    uint ReceiverPad2;
#include "Include/Frame.hlsli"

// Whether a sphere, relative to the view's CameraPos, touches the receivers' swept volume.
bool SphereInReceiverVolume(float3 centerRel, float radius)
{
    bool inside = true;
    [loop] for (uint i = 0u; i < ReceiverPlaneCount; i++)
        inside = inside && dot(ReceiverPlanes[i].xyz, centerRel) + ReceiverPlanes[i].w >= -radius;
    return inside;
}

#endif
