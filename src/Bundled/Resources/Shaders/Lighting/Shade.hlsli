// The light a surface sends toward the viewer, in nits, before exposure: the sun through its shadow, the point and spot
// lights that reach it, and the sky from everywhere. The lighting pass shades the G-buffer with it and a transparent
// surface itself, so both look the same. What it reads its draw or pass reads by these names, which the renderer
// declares: SkyCube, Dfg, Lights, LightBins, LightTiles and SkySh, and for shadows the virtual shadow maps
// (Include/VirtualShadows.hlsli: ShadowPool (comparison), ShadowTables and SunClipmap), without which it is lit. Where
// the world draws them, the lighting pass also reads contact shadows and ambient occlusion, each a texel per pixel of
// the view, and the probes of global illumination (Lighting/Gi.hlsli), which light from everywhere comes from in place
// of the sky alone.
//
// The lights reaching a pixel are found as Drobot 2017 ("Improved Culling for Tiled and Clustered Rendering") finds
// them: the view's lights are sorted nearest first, a z-bin (Include/Frame.hlsli's ZBinOf) holds the first and last of
// them that can reach its depths, and a screen tile a mask of those that reach it, so a pixel visits the masked bits
// between the two. The bits are merged across the wave so the loop is the same for every lane, and a light a lane did
// not need adds nothing, its reach ending where it does.

#ifndef MAGIC_SHADE_HLSLI
#define MAGIC_SHADE_HLSLI

#include "Include/Frame.hlsli"
#include "Include/Noise.hlsli"
#include "Include/Structs.hlsli"
#include "Lighting/Ambient.hlsli"
#include "Lighting/Ibl.hlsli"
#include "Lighting/Lights.hlsli"
#if defined(HAS_ShadowTables)
#include "Include/VirtualShadows.hlsli"
#endif
#if defined(HAS_GiIrradiance) && defined(HAS_GiDistance) && defined(HAS_GiLevels) && !SURFACE_FORWARD
#define SHADE_GI 1
#include "Lighting/Gi.hlsli"
#endif

// A surface to shade: where it is, camera-relative, what it is made of, and where on screen.
struct ShadePoint
{
    float3 position;
    float3 normal;
    float3 view;
    float3 baseColor;
    float metallic;
    float specular;
    float roughness;
    float occlusion;
    uint model;
    float2 modelData;
    float2 pixel;
    float viewDepth;
};

// The sun's share at a point: its virtual shadow map's, times what contact shadows, when the world draws them, say of
// the pixel an opaque surface was drawn at.
float SunShadow(float3 position, float3 normal, float viewDepth, float2 pixel)
{
    float lit = 1.0;
#if defined(HAS_ShadowTables)
    lit = SunVirtualShadow(position, normal, viewDepth);
#endif

#if defined(HAS_ContactShadows) && !SURFACE_FORWARD
    lit *= ContactShadows.Load(int3(pixel, 0));
#endif
    return lit;
}

float3 LocalLights(Shading shading, float3 position, float3 normal, float2 pixel, float viewDepth)
{
    float3 luminance = float3(0.0, 0.0, 0.0);
    uint bin = ZBinOf(viewDepth);
    uint first = LightBins.Load(bin * 8u);
    uint last = LightBins.Load(bin * 8u + 4u);
    uint2 tile = (uint2)pixel / LightTileSize;
    uint tileWords = (tile.y * TileCountX + tile.x) * TileWords;
    uint firstWord = WaveActiveMin(first <= last ? first >> 5u : 0xFFFFFFFFu);
    uint lastWord = WaveActiveMax(first <= last ? last >> 5u : 0u);
    [loop] for (uint word = firstWord; word <= lastWord && word < TileWords; word++)
    {
        uint mask = WaveActiveBitOr(LightTiles.Load((tileWords + word) * 4u) & RangeMask(word, first, last));
        [loop] while (mask != 0u)
        {
            uint bit = firstbitlow(mask);
            mask &= mask - 1u;
            GpuLight light = LOAD(Lights, GpuLight, word * 32u + bit);
            float3 toLight = light.position - position;

            // The tiles and bins list a light for every pixel they hold; one whose reach or cone ends short of this
            // pixel gives it nothing, so it is passed over before it is shaded.
            float distanceSq = dot(toLight, toLight);
            float spot = SpotAttenuation(toLight * rsqrt(max(distanceSq, 1e-8)), light.forward, float2(light.spotScale, light.spotOffset));
            [branch] if (distanceSq * light.invRangeSquared >= 1.0 || spot <= 0.0)
            {
                continue;
            }

            LightSample arriving = SphereLight(shading, toLight, light.intensity, light.radius, light.invRangeSquared);
            float3 lit = ShadeLight(shading, arriving) * spot;
#if defined(HAS_ShadowTables)
            [branch] if (light.shadow != NoShadowFace && any(lit > 0.0))
            {
                lit *= LightVirtualShadow(light.shadow, toLight, normal, viewDepth);
            }
#endif
            luminance += lit;
        }
    }

    return luminance;
}

float3 Shade(ShadePoint surface)
{
    float nov = max(dot(surface.normal, surface.view), 1e-4);
    float2 dfg = Dfg.SampleLevel(DfgSampler, float2(nov, surface.roughness), 0.0).xy;
    float sheen = Dfg.SampleLevel(DfgSampler, float2(nov, surface.modelData.y), 0.0).z;
    Shading shading = PrepareShading(surface.baseColor, surface.metallic, surface.specular, surface.roughness, surface.normal, surface.view, surface.model, surface.modelData, dfg, sheen);

    float3 luminance = float3(0.0, 0.0, 0.0);
    [branch] if (any(SunIlluminance > 0.0))
    {
        // A surface the sun gives nothing, turned from it, needs no look at its shadow.
        float3 sunlit = ShadeLight(shading, DirectionalLight(shading, SunDirection, SunIlluminance, SunAngularRadius));
        [branch] if (any(sunlit > 0.0))
        {
            luminance += sunlit * SunShadow(surface.position, surface.normal, surface.viewDepth, surface.pixel);
        }
    }

    [branch] if (LightCount > 0u)
    {
        luminance += LocalLights(shading, surface.position, surface.normal, surface.pixel, surface.viewDepth);
    }

    float3 sh[9];
    [unroll] for (uint i = 0u; i < 9u; i++)
    {
        sh[i] = LOAD(SkySh, float4, i).rgb;
    }

    float width;
    float height;
    float levels;
    SkyCube.GetDimensions(0u, width, height, levels);
    float3 reflected = reflect(-surface.view, surface.normal);
    float3 dominant = SpecularDominantDirection(surface.normal, reflected, surface.roughness);
    float3 prefiltered = SkyCube.SampleLevel(SkyCubeSampler, dominant, PrefilteredLevel(surface.roughness, levels)).rgb;
    float3 coatPrefiltered = SkyCube.SampleLevel(SkyCubeSampler, reflected, PrefilteredLevel(surface.modelData.y, levels)).rgb;
    // The material's own occlusion, and what the view's ambient occlusion found open around the pixel, whose middle,
    // the bent normal, light from everywhere arrives along and hides the reflections a crease would.
    float visibility = surface.occlusion;
    float3 open = surface.normal;
    float specularOcclusion = SpecularOcclusion(nov, visibility, surface.roughness);
#if defined(HAS_AmbientOcclusion) && !SURFACE_FORWARD
    float4 ambient = AmbientOcclusion.Load(int3(surface.pixel, 0));
    float3 bent = ambient.rgb * 2.0 - 1.0;
    visibility = min(visibility, ambient.a);
    open = dot(bent, bent) > 1e-4 ? normalize(bent) : surface.normal;
    specularOcclusion = SpecularOcclusionCone(open, visibility, reflected, surface.roughness);
#endif
    // Light from everywhere: the probes' where they hold the place, the sky's past them. Where they see less than the
    // open sky would give, inside, they hide that much of the sky's reflection too.
    float3 irradiance = ShIrradiance(open, sh);
#if defined(SHADE_GI)
    float held;
    float3 gathered = GiIrradianceAt(surface.position + CameraPosition, open, surface.view, held);
    float3 luminanceWeights = float3(0.2126, 0.7152, 0.0722);
    float skyShare = saturate(dot(gathered, luminanceWeights) / max(dot(irradiance, luminanceWeights), 1e-4));
    prefiltered *= lerp(1.0, skyShare, held);
    coatPrefiltered *= lerp(1.0, skyShare, held);
    irradiance = lerp(irradiance, gathered, held);
#endif
    float3 bounced = MultiBounceAo(visibility, shading.diffuseColor);
    luminance += ShadeAmbient(shading, irradiance * bounced, prefiltered, coatPrefiltered, 1.0, specularOcclusion);
    return luminance;
}

#endif
