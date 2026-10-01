// The one vertex shader of the mesh path, shared by every pipeline class. Vertex input is the fixed
// 48-byte vertex layout in slot 0 plus the instance index from an instance-rate buffer in slot 1: the index
// is never taken from SV_InstanceID, whose relation to first_instance differs between backends. The
// instance's transform rows come from the InstanceXforms table, its material and flags from Instances,
// and the clip position is camera-relative (world position minus CameraPos) before ViewProj.

#include "Include/Frame.hlsli"
#include "Include/Instance.hlsli"
#include "Include/Math.hlsli"
#include "Include/MeshVaryings.hlsli"

struct VsIn
{
    float3 position : TEXCOORD0;
    float3 normal : TEXCOORD1;
    float4 tangent : TEXCOORD2; // w = bitangent sign
    float2 uv : TEXCOORD3;
    uint instance : TEXCOORD4;  // per instance: the slot in the instance tables
};

MeshVaryings main(VsIn input)
{
    GpuInstanceXform world = InstanceXforms[input.instance];
    GpuInstance instance = Instances[input.instance];

    MeshVaryings output;
    output.worldPosition = TransformPosition(world, input.position);
    output.normal = TransformNormal(world, input.normal);
    output.tangent = float4(NormalizeOrZero(TransformDirection(world, input.tangent.xyz)), input.tangent.w);
    output.uv = input.uv;
    output.material = instance.materialSlot;
    output.flags = instance.flags;
    output.position = mul(ViewProj, float4(output.worldPosition - CameraPos, 1.0));
    return output;
}
