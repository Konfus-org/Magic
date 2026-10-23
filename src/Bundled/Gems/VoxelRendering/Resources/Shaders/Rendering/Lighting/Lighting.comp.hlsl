// The lighting, a thread a pixel of the view: what the G-buffer says is there, lit by the key light, the other
// directional lights and the sky from everywhere (its harmonics for what is spread out, its prefiltered cube for what
// is reflected), pre-exposed, with what the surface emits added. A pixel nothing was drawn on is left to the sky. A
// pixel lit to a NaN or an infinity, which every pass after this would spread, is drawn as the failure instead, and
// raised.

#include "Include/Color.hlsli"
#include "Include/Failure.hlsli"
#include "Include/GBuffer.hlsli"
#include "Lighting/Ambient.hlsli"
#include "Lighting/Ibl.hlsli"
#include "Lighting/Lights.hlsli"
#include "Rendering/Include/View.hlsli"

struct LightingConstants
{
    uint view;
    uint depth;
    uint albedo;
    uint normal;
    uint surface;            // the G-buffer's material target
    uint emissive;           // what the surfaces give off, pre-exposed
    uint dfg;
    uint sampler;            // linear, clamped
    uint sh;                 // the sky's harmonics, nine float4s
    uint specular;           // the sky prefiltered for each roughness
    uint exposure;           // the view's exposure (Effects/Exposure.comp.hlsl)
    uint hdr;                // written where something was drawn
};

CONSTANTS(LightingConstants, c);

// A surface to shade: where it is, camera-relative, what it is made of, and toward the camera.
struct ShadePoint
{
    float3 position;
    float3 view;
    GBufferSurface surface;
};

float3 Shade(ShadePoint at)
{
    GBufferSurface surface = at.surface;
    SamplerState linearClamp = SAMPLER_STATE(c.sampler);
    float nov = max(dot(surface.normal, at.view), 1e-4);
    float2 dfg = TEXTURE(c.dfg).SampleLevel(linearClamp, float2(nov, surface.roughness), 0.0).xy;
    float sheen = 0.0;
    [branch] if (surface.model == ShadingCloth)
    {
        sheen = TEXTURE(c.dfg).SampleLevel(linearClamp, float2(nov, surface.modelData.y), 0.0).z;
    }

    Shading shading = PrepareShading(surface.baseColor, surface.metallic, surface.specular, surface.roughness, surface.normal, at.view, surface.model, surface.modelData, dfg, sheen);

    float3 luminance = float3(0.0, 0.0, 0.0);
    [branch] if (any(View.keyLightIlluminance > 0.0))
    {
        luminance += ShadeLight(shading, DirectionalLight(shading, View.keyLightDirection, View.keyLightIlluminance, View.keyLightAngularRadius));
    }

    [loop] for (uint other = 0u; other < min(View.directionalCount, DirectionalLightCapacity); other++)
    {
        DirectionalLightConstants light = View.directional[other];
        luminance += ShadeLight(shading, DirectionalLight(shading, light.direction, light.illuminance, 0.0));
    }

    float3 sh[9];
    [unroll] for (uint i = 0u; i < 9u; i++)
    {
        sh[i] = BUFFER(c.sh).Load<float4>(i * 16u).rgb;
    }

    float width;
    float height;
    float levels;
    CUBE(c.specular).GetDimensions(0u, width, height, levels);
    float3 reflected = reflect(-at.view, surface.normal);
    float3 dominant = SpecularDominantDirection(surface.normal, reflected, surface.roughness);
    float3 prefiltered = CUBE(c.specular).SampleLevel(linearClamp, dominant, PrefilteredLevel(surface.roughness, levels)).rgb;
    float3 coatPrefiltered = float3(0.0, 0.0, 0.0);
    [branch] if (surface.model == ShadingClearCoat)
    {
        coatPrefiltered = CUBE(c.specular).SampleLevel(linearClamp, reflected, PrefilteredLevel(surface.modelData.y, levels)).rgb;
    }

    // The material's own occlusion hides the sky's light from everywhere, and its reflections a crease would.
    float specularOcclusion = SpecularOcclusion(nov, surface.occlusion, surface.roughness);
    float3 irradiance = ShIrradiance(surface.normal, sh) * MultiBounceAo(surface.occlusion, shading.diffuseColor);
    luminance += ShadeAmbient(shading, irradiance, prefiltered * specularOcclusion, coatPrefiltered * specularOcclusion);
    return luminance;
}

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    LoadView(c.view);
    int3 pixel = int3(id.xy, 0);
    if (any(id.xy >= (uint2)View.viewSize))
    {
        return;
    }

    float depth = TEXTURE(c.depth).Load(pixel).r;
    if (depth <= 0.0)
    {
        return;
    }

    float viewDepth = ViewDepth(depth);
    ShadePoint at;
    at.position = ViewToWorld(ViewPosition(float2(id.xy) + 0.5, viewDepth));
    at.view = -normalize(at.position);
    at.surface = DecodeGBuffer(TEXTURE(c.albedo).Load(pixel), TEXTURE(c.normal).Load(pixel), TEXTURE(c.surface).Load(pixel));
    float3 lit = Shade(at) * BUFFER(c.exposure).Load<float4>(0u).x + TEXTURE(c.emissive).Load(pixel).rgb;
    [branch] if (any(isnan(lit)) || any(isinf(lit)))
    {
        RaiseFailure(BUFFER(View.failures), FailureNaN);
        lit = FailurePixel(View.time);
    }

    LEVEL(c.hdr, 0u)[id.xy] = float4(lit, 1.0);
}
