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

// How much of a light's colour the surface sends to the camera: the diffuse for light arriving from toDiffuse and
// the highlight for light arriving from toHighlight, each zero where the surface faces away. The two differ for an
// area light (Lighting/Common.hlsli's LightNearest and LightAlong), whose highlight is where the reflection meets it.
float3 Reflected(GBufferSurface surface, float3 toDiffuse, float3 toHighlight, float3 toCamera)
{
    float3 halfway = NormalizeOrZero(toHighlight + toCamera);
    float shininess = lerp(ShininessSmooth, ShininessRough, surface.roughness);
    float highlight = pow(saturate(dot(surface.normal, halfway)), shininess);
    float specular = highlight * (1.0 - surface.roughness) * SpecularStrength;
    float3 diffuseColor = surface.baseColor * (1.0 - surface.metallic);
    float3 specularColor = lerp(float3(1.0, 1.0, 1.0), surface.baseColor, surface.metallic);

    return diffuseColor * surface.occlusion * saturate(dot(surface.normal, toDiffuse))
        + specularColor * specular * saturate(dot(surface.normal, toHighlight));
}

// The same for light that arrives from one direction, as the sun's and a point's do.
float3 Reflected(GBufferSurface surface, float3 toLight, float3 toCamera)
{
    return Reflected(surface, toLight, toLight, toCamera);
}

#endif
