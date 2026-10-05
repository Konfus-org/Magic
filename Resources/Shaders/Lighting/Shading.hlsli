// How a surface sends light on to the camera: what the deferred lighting (Lighting/Lighting.comp.hlsl) and the
// forward transparency (Templates/Forward.frag.hlsl) share, so a transparent surface is lit exactly as an opaque
// one would be. Lambert plus a Blinn-Phong highlight sized by roughness.

#ifndef MAGIC_SHADING_HLSLI
#define MAGIC_SHADING_HLSLI

#include "Include/GBuffer.hlsli"

// The Blinn-Phong exponent of a mirror-smooth and of a fully rough surface, and the highlight's strength.
static const float ShininessSmooth = 256.0;
static const float ShininessRough = 4.0;
static const float SpecularStrength = 0.5;

// How much of a light's colour the surface sends to the camera, for light arriving from toLight: the
// diffuse and the highlight together, zero when the surface faces away.
float3 Reflected(GBufferSurface surface, float3 toLight, float3 toCamera)
{
    float3 halfway = NormalizeOrZero(toLight + toCamera);
    float facing = saturate(dot(surface.normal, toLight));

    float shininess = lerp(ShininessSmooth, ShininessRough, surface.roughness);
    float highlight = pow(saturate(dot(surface.normal, halfway)), shininess);
    float specular = highlight * (1.0 - surface.roughness) * SpecularStrength;
    float3 diffuseColor = surface.baseColor * (1.0 - surface.metallic);
    float3 specularColor = lerp(float3(1.0, 1.0, 1.0), surface.baseColor, surface.metallic);

    return (diffuseColor * surface.occlusion + specularColor * specular) * facing;
}

#endif
