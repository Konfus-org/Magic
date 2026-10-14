// The G-buffer: what a surface is, written by the surface draws and read back by the lighting, and nothing else knows
// how it is packed. Four targets, as many as a render pass draws into, the fourth the scene colour itself, which the
// surface's emission is written into and the lighting adds to:
//
//   Albedo    rgba8 sRGB  base colour; a occlusion
//   Normal    rgb10a2     rg the world normal on the octahedron (Include/Packing.hlsli), b roughness, a shading model
//   Material  rgba8       metallic, specular, and the shading model's two values
//   Hdr       rgba16f     emission, pre-exposed

#ifndef MAGIC_GBUFFER_HLSLI
#define MAGIC_GBUFFER_HLSLI

#include "Include/Packing.hlsli"

struct GBufferOutput
{
    float4 albedo : SV_Target0;
    float4 normal : SV_Target1;
    float4 material : SV_Target2;
    float4 emissive : SV_Target3;
};

struct GBufferSurface
{
    float3 baseColor;
    float occlusion;
    float3 normal;
    float roughness;
    uint model;
    float metallic;
    float specular;
    float2 modelData;
};

GBufferOutput EncodeGBuffer(GBufferSurface surface, float3 emissive)
{
    GBufferOutput output;
    output.albedo = float4(surface.baseColor, surface.occlusion);
    output.normal = float4(OctEncode(surface.normal) * 0.5 + 0.5, saturate(surface.roughness), (float)surface.model / 3.0);
    output.material = float4(surface.metallic, surface.specular, surface.modelData);
    output.emissive = float4(emissive, 1.0);
    return output;
}

GBufferSurface DecodeGBuffer(float4 albedo, float4 normal, float4 material)
{
    GBufferSurface surface;
    surface.baseColor = albedo.rgb;
    surface.occlusion = albedo.a;
    surface.normal = OctDecode(normal.xy * 2.0 - 1.0);
    surface.roughness = normal.z;
    surface.model = (uint)round(normal.w * 3.0);
    surface.metallic = material.r;
    surface.specular = material.g;
    surface.modelData = material.ba;
    return surface;
}

#endif
