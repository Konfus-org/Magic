// The shadow pass's vertex stage: an instance's transform from InstanceXforms and the shadow view's ViewProj,
// nothing else. Its own input (the vertex position from slot 0, the instance slot from the instance-rate buffer in
// slot 1: Shadows.VertexAttributes) and only SV_Position out, so the empty fragment stage has no varyings to line up
// with. The one shadow pipeline draws every opaque class with this.

#include "Include/Frame.hlsli"
#include "Include/Structs.hlsli"

StructuredBuffer<GpuInstanceXform> InstanceXforms : READ(0);

struct VsIn
{
    float3 position : TEXCOORD0;
    uint instance : TEXCOORD1;
};

float4 main(VsIn input) : SV_Position
{
    GpuInstanceXform world = InstanceXforms[input.instance];
    float4 point4 = float4(input.position, 1.0);
    float3 worldPosition = float3(dot(world.r0, point4), dot(world.r1, point4), dot(world.r2, point4));
    return mul(ViewProj, float4(worldPosition - CameraPos, 1.0));
}
