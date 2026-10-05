// The gbuffer template: the renderer composes it after a surface shader and its generated
// LoadMaterialParams, evaluates the surface and writes what it is into the gbuffer (Include/GBuffer.hlsli).
// Nothing is lit here: Lighting/Lighting.comp.hlsl does that afterwards, once per pixel, from what this
// wrote. It knows nothing about failures: a broken material or model is drawn with
// Surfaces/Failure.surf.hlsl, a surface like any other.

#include "Include/GBuffer.hlsli"
#include "Include/MeshFragment.hlsli"
#include "Include/MeshVaryings.hlsli"
#include "Include/Surface.hlsli"

GBufferOutput main(MeshVaryings input, bool isFrontFace : SV_IsFrontFace)
{
    SurfaceInputs surfaceInputs = SurfaceInputsOf(input, isFrontFace);
    MaterialParams material = LoadMaterialParams(input.material);
    Surface surface = EvaluateSurface(surfaceInputs, material);

#if SURFACE_MASKED
    clip(surface.alpha - surface.alphaCutoff);
#endif
    clip(LodCoverage(input.lodFade, input.position.xy));

    GBufferSurface stored;
    stored.emissive = surface.emissive + KeepBindingsAlive(input);
    stored.baseColor = surface.baseColor;
    stored.normal = NormalizeOrZero(surface.normal);
    stored.roughness = surface.roughness;
    stored.metallic = surface.metallic;
    stored.occlusion = surface.occlusion;
    return EncodeGBuffer(stored);
}
