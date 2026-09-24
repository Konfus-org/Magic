// The one vertex shader of the mesh path (M2; becomes GBuffer.vert at M5). Vertex input is the fixed
// StaticVertex layout in slot 0 plus the instance index from an instance-rate buffer in slot 1: the index
// is never taken from SV_InstanceID, whose relation to first_instance differs between backends. The
// instance's transform rows come from the InstanceXform table, its material from InstanceCull, and the
// clip position is camera-relative (world position minus CameraPos) before ViewProj.
#include "Include/Bindings.hlsli"
#include "Include/Structs.hlsli"
#define FRAME_REGISTER VS_CB(0)
#include "Include/Frame.hlsli"
#include "Include/Instance.hlsli"

struct VsIn
{
    float3 position : TEXCOORD0;
    float3 normal : TEXCOORD1;
    float4 tangent : TEXCOORD2; // w = bitangent sign
    float2 uv : TEXCOORD3;
    uint instance : TEXCOORD4;  // per instance
};

struct VsOut
{
    float3 worldPos : TEXCOORD0;
    float3 normal : TEXCOORD1;
    float4 tangent : TEXCOORD2;
    float2 uv : TEXCOORD3;
    nointerpolation uint material : TEXCOORD4;
    float4 position : SV_Position;
};

VsOut main(VsIn input)
{
    GpuInstanceXform world = LoadWorld(input.instance);
    GpuInstanceCull cull = InstanceCull[input.instance];

    VsOut output;
    output.worldPos = TransformPosition(world, input.position);
    output.normal = TransformNormal(world, input.normal);
    output.tangent = float4(normalize(TransformDirection(world, input.tangent.xyz)), input.tangent.w);
    output.uv = input.uv;
    output.material = cull.materialSlot;
    output.position = mul(ViewProj, float4(output.worldPos - CameraPos.xyz, 1.0));
    return output;
}
