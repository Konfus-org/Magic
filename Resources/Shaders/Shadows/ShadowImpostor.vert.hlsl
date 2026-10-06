// The impostor cards' shadow pass, vertex stage: Shadow.vert.hlsl for a card (Include/Impostor.hlsli), turned to face
// the light about the model's up axis instead of the camera (a cascade's light comes from along the view's backward
// axis, a local light's from its eye), so the shadow is the model's outline as the light sees it, and pushed back from
// the light by half its side, behind the camera's card, which would otherwise shadow itself. The fragment stage
// (ShadowImpostor.frag.hlsl) keeps only what the baked coverage covers. The same tile placement and clip planes.

#include "Include/Frame.hlsli"
#include "Include/Impostor.hlsli"
#include "Include/ShadowViews.hlsli"

StructuredBuffer<GpuInstanceXform> InstanceXforms : READ(0);
StructuredBuffer<GpuShadowView> ShadowViews : READ(1);
StructuredBuffer<uint> ShadowRefreshed : READ(2);

struct VsIn
{
    float3 position : TEXCOORD0;
    float3 normal : TEXCOORD1;
    float4 tangent : TEXCOORD2;
    uint instance : TEXCOORD3;
};

struct VsOut
{
    float4 position : SV_Position;
    float4 clip : SV_ClipDistance0;
    float2 frameUv : TEXCOORD0;
    nointerpolation uint4 impostor : TEXCOORD1; // the atlases, the frames, asuint(blend)
};

VsOut main(VsIn input)
{
    GpuShadowView view = ShadowViews[ShadowRefreshed[PassIteration()]];
    GpuInstanceXform world = InstanceXforms[input.instance & GpuVisibleSlotMask];

    // The light: a cascade's from back along its forward axis, a local light's from its eye.
    bool isOrthographic = (view.flags.x & ShadowViewOrthographic) != 0u;
    float4 light = isOrthographic ? float4(-view.rotation[2].xyz, 0.0) : float4(view.eye.xyz, 1.0);
    ImpostorCorner corner = ImpostorCornerOf(world, input.position, input.normal, input.tangent, light, 1.0);
    float4 clip = mul(view.viewProj, float4(corner.position - view.eye.xyz, 1.0));

    VsOut output;
    output.clip = float4(clip.w + clip.x, clip.w - clip.x, clip.w + clip.y, clip.w - clip.y);
    float2 origin = view.tileUv.xy;
    float2 size = view.tileUv.zw;
    output.position = float4(
        clip.x * size.x + (2.0 * origin.x + size.x - 1.0) * clip.w,
        clip.y * size.y + (1.0 - 2.0 * origin.y - size.y) * clip.w,
        clip.z,
        clip.w);
    output.frameUv = corner.frameUv;
    output.impostor = uint4(ImpostorAtlases(input.tangent), corner.frames, asuint(corner.blend));
    return output;
}
