// The lighting, a thread a pixel of the view: what the G-buffer says is there, lit by the world's lights
// (Lighting/Lights) and the sky from everywhere (Lighting/SkyLight), pre-exposed by the view's exposure, with what the
// surface emits added. A pixel nothing was drawn on is left to the sky. A pixel lit to a NaN or an infinity, which every
// pass after this would spread, is drawn as the failure instead, and raised.

module "Renderer/Lighting";

import Core.Failure;
import Core.GBuffer;
import Lighting.SkyLight;
import Renderer.World;

// The pipeline script's LightingConstants, member for member.
struct LightingConstants
{
    GBuffer gbuffer;
    SkyLight sky;
    TextureRef hdr;          // written where something was drawn
}

// A surface to shade: where it is, camera-relative, what it is made of, and toward the camera.
struct ShadePoint
{
    float3 position;
    float3 view;
    GBufferSurface surface;
}

// The luminance a surface sends toward the camera from the world's lights and the sky.
float3 ShadeSurface(SkyLight sky, ShadePoint at)
{
    GBufferSurface surface = at.surface;
    float nov = max(dot(surface.normal, at.view), 1e-4);
    float2 dfg = sky.GetDfg(nov, surface.roughness).xy;
    float sheen = 0.0;
    [branch] if (surface.model == ShadingModel.Cloth)
    {
        sheen = sky.GetDfg(nov, surface.modelData.y).z;
    }

    Shading shading = CreateShading(
        surface.baseColor,
        surface.metallic,
        surface.specular,
        surface.roughness,
        surface.normal,
        at.view,
        surface.model,
        surface.modelData,
        dfg,
        sheen);
    float3 luminance = ShadeLights(World.lights, shading, at.position, Unshadowed());

    float3 reflected = reflect(-at.view, surface.normal);
    float3 dominant = GetDominantDirection(surface.normal, reflected, surface.roughness);
    float3 prefiltered = sky.SampleSpecular(dominant, surface.roughness);
    float3 coatPrefiltered = float3(0.0, 0.0, 0.0);
    [branch] if (surface.model == ShadingModel.ClearCoat)
    {
        coatPrefiltered = sky.SampleSpecular(reflected, surface.modelData.y);
    }

    // The material's own occlusion hides the sky's light from everywhere, and its reflections a crease would.
    float specularOcclusion = GetSpecularOcclusion(nov, surface.occlusion, surface.roughness);
    float3 irradiance = sky.GetIrradiance(surface.normal) * GetMultiBounceAo(surface.occlusion, shading.diffuseColor);
    luminance += ShadeAmbient(shading, irradiance, prefiltered * specularOcclusion, coatPrefiltered * specularOcclusion);
    return luminance;
}

[shader("compute")]
[numthreads(8, 8, 1)]
void ShadeLighting(uint3 id : SV_DispatchThreadID, uniform PassHeader pass, uniform LightingConstants c)
{
    BeginPass(pass);
    int2 pixel = int2(id.xy);
    if (any(id.xy >= (uint2)View.viewSize))
    {
        return;
    }

    float depth = c.gbuffer.depth.Load(pixel, 0).r;
    if (depth <= 0.0)
    {
        return;
    }

    ShadePoint at;
    at.position = View.ToWorld(View.ToViewPosition(float2(id.xy) + 0.5, View.GetViewDepth(depth)));
    at.view = -normalize(at.position);
    at.surface = c.gbuffer.Load(pixel);
    float3 lit = ShadeSurface(c.sky, at) * View.exposure[0u].x + c.gbuffer.emissive.Load(pixel, 0).rgb;
    [branch] if (any(isnan(lit)) || any(isinf(lit)))
    {
        RaiseFailure(World.failures, FailureCode.NaN);
        lit = GetFailureGlow(World.clocks.realSec);
    }

    c.hdr.GetLevel(0u)[id.xy] = float4(lit, 1.0);
}
