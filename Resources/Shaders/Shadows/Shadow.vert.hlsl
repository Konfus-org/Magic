// The shadow pass's vertex stage: an instance's transform from InstanceXforms and the shadow view this iteration
// draws (the row of the refreshed list at PassIteration(), Include/ShadowViews.hlsli), nothing else. Positions are taken relative to the
// view's eye, projected, and moved into the view's tile of the atlas, which is drawn whole with one viewport: the
// tile's edges are clip planes (SV_ClipDistance), so a caster never spills into the next tile. Its own input (the
// vertex position from slot 0, the instance slot from the instance-rate buffer in slot 1) and only system values
// out, so the empty fragment stage has no varyings to line up with. The one shadow pipeline draws every opaque
// class with this.

#include "Include/Frame.hlsli"
#include "Include/ShadowViews.hlsli"

StructuredBuffer<GpuInstanceXform> InstanceXforms : READ(0);
StructuredBuffer<GpuShadowView> ShadowViews : READ(1);
StructuredBuffer<uint> ShadowRefreshed : READ(2);

struct VsIn
{
    float3 position : TEXCOORD0;
    uint instance : TEXCOORD1;
};

struct VsOut
{
    float4 position : SV_Position;
    float4 clip : SV_ClipDistance0;
};

VsOut main(VsIn input)
{
    GpuShadowView view = ShadowViews[ShadowRefreshed[PassIteration()]];
    GpuInstanceXform world = InstanceXforms[input.instance];
    float4 point4 = float4(input.position, 1.0);
    float3 worldPosition = float3(dot(world.r0, point4), dot(world.r1, point4), dot(world.r2, point4));
    float4 clip = mul(view.viewProj, float4(worldPosition - view.eye.xyz, 1.0));

    // Inside the tile: -w <= x <= w and -w <= y <= w of the view's own clip space, as four distances.
    VsOut output;
    output.clip = float4(clip.w + clip.x, clip.w - clip.x, clip.w + clip.y, clip.w - clip.y);

    // The tile's place in the atlas, as the same homogeneous coordinates: uv = origin + (ndc * 0.5 + 0.5) * size
    // along x, flipped along y, then back to NDC over the whole atlas.
    float2 origin = view.tileUv.xy;
    float2 size = view.tileUv.zw;
    output.position = float4(
        clip.x * size.x + (2.0 * origin.x + size.x - 1.0) * clip.w,
        clip.y * size.y + (1.0 - 2.0 * origin.y - size.y) * clip.w,
        clip.z,
        clip.w);
    return output;
}
