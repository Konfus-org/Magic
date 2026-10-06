// The one vertex shader of the mesh path, shared by every pipeline class. Vertex input is the fixed
// 48-byte vertex layout in slot 0 plus the instance index and its LOD fade (GpuVisible) from an instance-rate
// buffer in slot 1: the index is never taken from SV_InstanceID, whose relation to first_instance differs
// between backends. The
// instance's transform rows come from the InstanceXforms table, its material and flags from Instances,
// and the clip position is camera-relative (world position minus CameraPos) before ViewProj.

#include "Include/Frame.hlsli"
#include "Include/Impostor.hlsli"
#include "Include/Instance.hlsli"
#include "Include/Math.hlsli"
#include "Include/MeshVaryings.hlsli"

struct VsIn
{
    float3 position : TEXCOORD0;
    float3 normal : TEXCOORD1;
    float4 tangent : TEXCOORD2; // w = bitangent sign
    float2 uv : TEXCOORD3;
    uint instance : TEXCOORD4;  // per instance: the slot in the instance tables (GpuVisible.slot)
    float lodFade : TEXCOORD5;  // per instance: GpuVisible.lodFade
};

MeshVaryings main(VsIn input)
{
    uint slot = input.instance & GpuVisibleSlotMask;
    GpuInstanceXform world = InstanceXforms[slot];
    GpuInstance instance = Instances[slot];

    MeshVaryings output;
    [branch] if (IsImpostorCard(input.tangent))
    {
        // The camera: a place, or for an orthographic view the way back along its forward axis (View's rows are its axes).
        float4 camera = IsOrthographic != 0u ? float4(-View[2].xyz, 0.0) : float4(CameraPos, 1.0);
        ImpostorCorner corner = ImpostorCornerOf(world, input.position, input.normal, input.tangent, camera, 0.0);

        output.worldPosition = corner.position;
        output.normal = NormalizeOrZero(corner.axisX); // the baked model's X in the world
        output.tangent = float4(NormalizeOrZero(float3(world.r0.y, world.r1.y, world.r2.y)), 0.0); // and its Y
        output.uv = corner.frameUv;
        output.impostor = uint4(ImpostorAtlases(input.tangent), corner.frames, asuint(corner.blend));
    }
    else
    {
        output.worldPosition = TransformPosition(world, input.position);
        output.normal = TransformNormal(world, input.normal);
        output.tangent = float4(NormalizeOrZero(TransformDirection(world, input.tangent.xyz)), input.tangent.w);
        output.uv = input.uv;
        output.impostor = uint4(0u, 0u, 0u, 0u);
    }

    output.material = instance.materialSlot;
    output.flags = instance.flags | (input.instance & ~GpuVisibleSlotMask); // the level of detail in the top bits, for the debug view
    output.lodFade = input.lodFade;
    output.position = mul(ViewProj, float4(output.worldPosition - CameraPos, 1.0));
    return output;
}
