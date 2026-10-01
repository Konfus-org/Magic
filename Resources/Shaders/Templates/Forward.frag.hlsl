// The forward template: the renderer composes it after a surface shader and its generated
// LoadMaterialParams, evaluates the surface and shades it with the sun (Lambert plus a Blinn-Phong
// highlight sized by roughness) and a flat ambient, into the linear Hdr target. It knows nothing about
// failures: a broken material or model is drawn with Surfaces/Failure.surf.hlsl, a surface like any other.

#include "Include/Frame.hlsli"
#include "Include/MeshVaryings.hlsli"
#include "Include/Surface.hlsli"

// The Blinn-Phong exponent of a mirror-smooth and of a fully rough surface, and the highlight's strength.
static const float ShininessSmooth = 256.0;
static const float ShininessRough = 4.0;
static const float SpecularStrength = 0.5;

// Keeps every pool and every interpolant alive whatever the surface reads; returns zero. DXC drops
// resources and inputs nothing uses; SDL lays a stage's storage buffers out after its samplers, so a
// surface that samples nothing would leave READ(0)..READ(7) missing and the Materials buffer at the wrong
// slot; and on the DXIL path the fragment signature is rebuilt from the inputs that survive, so a dropped
// input (a surface that ignores the tangent, say) shifts the registers away from the vertex stage's and the
// pipeline fails to link. The condition never holds (no instance has every flag set) and the texture
// reference is a runtime value, so nothing folds away: one SampleTextureGrad touches all eight pools.
float3 KeepBindingsAlive(MeshVaryings input)
{
    float3 nothing = float3(0.0, 0.0, 0.0);
    [branch] if (input.flags == 0xFFFFFFFFu)
    {
        nothing = SampleTextureGrad(input.flags, input.uv, float2(0.0, 0.0), float2(0.0, 0.0)).rgb
            + input.tangent.xyz + input.worldPosition + input.normal + float3(input.uv, asfloat(input.material));
    }

    return nothing;
}

// The sun (Lambert plus a Blinn-Phong highlight) and a flat ambient, plus what the surface emits.
float3 Shade(Surface surface, float3 worldPosition)
{
    float3 toCamera = normalize(CameraPos - worldPosition);
    float3 toSun = -SunDirection;
    float3 halfway = normalize(toSun + toCamera);
    float sunFacing = saturate(dot(surface.normal, toSun));

    float shininess = lerp(ShininessSmooth, ShininessRough, surface.roughness);
    float highlight = pow(saturate(dot(surface.normal, halfway)), shininess);
    float specular = highlight * (1.0 - surface.roughness) * SpecularStrength;
    float3 diffuseColor = surface.baseColor * (1.0 - surface.metallic);
    float3 specularColor = lerp(float3(1.0, 1.0, 1.0), surface.baseColor, surface.metallic);

    float3 diffuseLight = diffuseColor * (SunColor * sunFacing + Ambient) * surface.occlusion;
    float3 specularLight = SunColor * specular * specularColor * sunFacing;
    return diffuseLight + specularLight + surface.emissive;
}

float4 main(MeshVaryings input, bool isFrontFace : SV_IsFrontFace) : SV_Target0
{
    SurfaceInputs surfaceInputs;
    surfaceInputs.worldPosition = input.worldPosition;
    surfaceInputs.normal = normalize(input.normal);
    surfaceInputs.tangent = input.tangent;
    surfaceInputs.uv = input.uv;
    surfaceInputs.uvDdx = ddx(input.uv);
    surfaceInputs.uvDdy = ddy(input.uv);
    surfaceInputs.screenUv = input.position.xy * ViewTexel;
    surfaceInputs.time = Time;

    // A mirrored instance's winding is inverted, so what the rasteriser calls front is its back.
    bool isMirrored = (input.flags & InstanceMirrored) != 0u;
    surfaceInputs.isFrontFace = isFrontFace != isMirrored;
#if SURFACE_DOUBLE_SIDED
    surfaceInputs.normal = surfaceInputs.isFrontFace ? surfaceInputs.normal : -surfaceInputs.normal;
#endif

    MaterialParams material = LoadMaterialParams(input.material);
    Surface surface = EvaluateSurface(surfaceInputs, material);

#if SURFACE_MASKED
    clip(surface.alpha - surface.alphaCutoff);
#endif

    surface.emissive += KeepBindingsAlive(input);
    return float4(Shade(surface, input.worldPosition), 1.0);
}
