// The gbuffer: what the scene is drawn into before it is lit, mirrored by Gems/DeferredRenderer/GBuffer.cs.
// Every surface in view is stored as the material it is, one target per property, and nothing in it is lit:
//
//   Emissive  r11g11b10f  what the surface emits, linear; the clear colour where nothing was drawn
//   Albedo    rgba8 sRGB  base colour
//   Normal    rgb10a2     world-space normal, -1..1 packed into 0..1
//   Material  rgba8       r roughness, g metallic, b occlusion
//   Depth                 reverse-Z; 0 where nothing was drawn
//
// The mesh path writes it (EncodeGBuffer, from Templates/GBuffer.frag.hlsl) and the lighting reads it back
// (DecodeGBuffer); nothing else knows the packing. There is no position target: where a pixel is comes from
// its depth and the frame block (ViewPosition).

#ifndef MAGIC_GBUFFER_HLSLI
#define MAGIC_GBUFFER_HLSLI

#include "Include/Frame.hlsli"
#include "Include/Math.hlsli"

// One pixel of the gbuffer, unpacked.
struct GBufferSurface
{
    float3 emissive;
    float3 baseColor;
    float3 normal; // world space, normalised
    float roughness;
    float metallic;
    float occlusion;
};

// What a fragment shader of the mesh path returns: one colour target per gbuffer texture, in GBuffer.ColorFormats' order.
struct GBufferOutput
{
    float4 emissive : SV_Target0;
    float4 albedo : SV_Target1;
    float4 normal : SV_Target2;
    float4 material : SV_Target3;
};

GBufferOutput EncodeGBuffer(GBufferSurface surface)
{
    GBufferOutput output;
    output.emissive = float4(surface.emissive, 1.0);
    output.albedo = float4(surface.baseColor, 1.0);
    output.normal = float4(surface.normal * 0.5 + 0.5, 1.0);
    output.material = float4(surface.roughness, surface.metallic, surface.occlusion, 1.0);
    return output;
}

// From one texel of each colour target.
GBufferSurface DecodeGBuffer(float3 emissive, float3 albedo, float3 normal, float3 material)
{
    GBufferSurface surface;
    surface.emissive = emissive;
    surface.baseColor = albedo;
    surface.normal = NormalizeOrZero(normal * 2.0 - 1.0);
    surface.roughness = material.r;
    surface.metallic = material.g;
    surface.occlusion = material.b;
    return surface;
}

// The distance along the view's +Z a depth value stands for. Only for a depth above 0: 0 is nothing drawn.
// Perspective: depth = near (far - z) / (z (far - near)), which with no far plane is near / z.
float ViewDepth(float depth)
{
    float nearOverFar = Near / Far;
    float perspective = Near / (depth * (1.0 - nearOverFar) + nearOverFar);
    return IsOrthographic != 0u ? lerp(Far, Near, depth) : perspective;
}

// A view-space position from a point of the view's rectangle (in pixels from its top left corner, so a
// pixel's centre is its coordinates plus a half) and the view depth there.
float3 ViewPosition(float2 viewPixel, float viewDepth)
{
    float2 across = viewPixel * ViewTexel;
    float2 ndc = float2(across.x * 2.0 - 1.0, 1.0 - across.y * 2.0);
    float spread = IsOrthographic != 0u ? 1.0 : viewDepth;
    return float3(ndc / ProjScale * spread, viewDepth);
}

// A view-space vector in world space. View is a rotation, so its transpose undoes it; for a position this is
// relative to the camera (add CameraPos for the absolute one, at the cost of precision far from the origin).
float3 ViewToWorld(float3 viewVector)
{
    return mul(float4(viewVector, 0.0), View).xyz;
}

#endif
