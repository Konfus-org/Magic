// The light a surface sends toward the viewer, in nits, before exposure: the key light through its shadow (the sun by
// day, the moon by night, else the brightest other directional light), the other directional lights, unshadowed, the
// point and spot lights that reach it, and the sky from everywhere. The lighting pass shades the G-buffer with it and a transparent
// surface itself, so both look the same. What it reads its draw or pass reads by these names, which the renderer
// declares: SkySpecular (the sky prefiltered for each roughness), Dfg, Lights, LightBins, LightTiles and SkySh, and for
// shadows the virtual shadow maps (Include/VirtualShadows.hlsli: ShadowPool (comparison), ShadowTables and
// ShadowClipmap), without which it is lit. Where the world draws them, the lighting pass also reads contact shadows,
// ambient occlusion and traced reflections, each a texel per pixel of the view, the reflections in place of the sky's
// where they have them, and the probes of global illumination (Lighting/Gi.hlsli), which light from everywhere comes
// from in place of the sky alone, and which a surface reflects where nothing traced its reflection: their light along
// it, the sky's detail kept by as much of the sky as they see that way. A sun or a moon reaches it through the sky's
// air and clouds, the sky's shadow, where the frame draws one (SkyShadow).
//
// The lights reaching a pixel are found as Drobot 2017 ("Improved Culling for Tiled and Clustered Rendering") finds
// them: the view's lights are sorted nearest first, a z-bin holds the first and last of them that can reach its depths,
// and a screen tile a mask of those that reach it, so a pixel visits the masked bits between the two
// (Include/LightCulling.hlsli's LightWalk), the same bits for every lane of the wave.

#ifndef MAGIC_SHADE_HLSLI
#define MAGIC_SHADE_HLSLI

#include "Include/Color.hlsli"
#include "Include/Frame.hlsli"
#include "Include/LightCulling.hlsli"
#include "Include/Noise.hlsli"
#include "Include/SkyShadow.hlsli"
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

// The key light's share at a point, by colour: what the sky lets through of it there, for a sun or moon
// (Include/SkyShadow.hlsli), times its virtual shadow map's, its level picked by a dither that moves each frame, which
// the temporal upscale blends, times what contact shadows, when the world draws them, say of the pixel an opaque
// surface was drawn at.
float3 KeyLightShadow(float3 position, float3 normal, float viewDepth, float2 pixel)
{
    float3 lit = SkyShadowAt(position);
#if defined(HAS_ShadowTables)
    lit *= KeyLightVirtualShadow(position, normal, viewDepth, InterleavedGradientNoise(pixel + float2(17.0, 59.0), FrameIndex));
#endif

#if defined(HAS_ContactShadows) && !SURFACE_FORWARD
    lit *= ContactShadows.Load(int3(pixel, 0));
#endif
    return lit;
}

// The directional lights besides the key light, each lighting the surface whole, with no shadow and no disc.
float3 OtherDirectionalLights(Shading shading)
{
    float3 luminance = float3(0.0, 0.0, 0.0);
    [loop] for (uint i = 0u; i < min(DirectionalCount, DirectionalLightCapacity); i++)
    {
        DirectionalLightConstants light = Directional[i];
        luminance += ShadeLight(shading, DirectionalLight(shading, light.Direction, light.Illuminance, 0.0));
    }

    return luminance;
}

float3 LocalLights(Shading shading, float3 position, float3 normal, float2 pixel, float viewDepth)
{
    float3 luminance = float3(0.0, 0.0, 0.0);
    LightWalk walk = LightWalkOf(pixel, viewDepth);
    [loop] for (uint word = walk.firstWord; word <= walk.lastWord && word < TileWords; word++)
    {
        uint mask = LightWalkMask(walk, word);
        [loop] while (mask != 0u)
        {
            uint bit = firstbitlow(mask);
            mask &= mask - 1u;
            GpuLight light = LOAD(Lights, GpuLight, WaveReadLaneFirst(word * 32u + bit));
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
    float sheen = 0.0;
    [branch] if (surface.model == ShadingCloth)
    {
        sheen = Dfg.SampleLevel(DfgSampler, float2(nov, surface.modelData.y), 0.0).z;
    }

    Shading shading = PrepareShading(surface.baseColor, surface.metallic, surface.specular, surface.roughness, surface.normal, surface.view, surface.model, surface.modelData, dfg, sheen);

    float3 luminance = float3(0.0, 0.0, 0.0);
    [branch] if (any(KeyLightIlluminance > 0.0))
    {
        // A surface the light gives nothing, turned from it, needs no look at its shadow.
        float3 keyLit = ShadeLight(shading, DirectionalLight(shading, KeyLightDirection, KeyLightIlluminance, KeyLightAngularRadius));
        [branch] if (any(keyLit > 0.0))
        {
            luminance += keyLit * KeyLightShadow(surface.position, surface.normal, surface.viewDepth, surface.pixel);
        }
    }

    [branch] if (DirectionalCount > 0u)
    {
        luminance += OtherDirectionalLights(shading);
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
    SkySpecular.GetDimensions(0u, width, height, levels);
    float3 reflected = reflect(-surface.view, surface.normal);
    float3 dominant = SpecularDominantDirection(surface.normal, reflected, surface.roughness);
    float3 prefiltered = SkySpecular.SampleLevel(SkySpecularSampler, dominant, PrefilteredLevel(surface.roughness, levels)).rgb;
    float3 coatPrefiltered = float3(0.0, 0.0, 0.0);
    [branch] if (surface.model == ShadingClearCoat)
    {
        coatPrefiltered = SkySpecular.SampleLevel(SkySpecularSampler, reflected, PrefilteredLevel(surface.modelData.y, levels)).rgb;
    }

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
    // What the surface reflects, traced, where it is smooth enough to have it, in place of the sky's.
    float4 traced = float4(0.0, 0.0, 0.0, 0.0);
#if defined(HAS_Reflections) && !SURFACE_FORWARD
    traced = Reflections.Load(int3(surface.pixel, 0));
#endif

    // Light from everywhere: the probes' where they hold the place, the sky's past them.
    float3 irradiance = ShIrradiance(open, sh);
#if defined(SHADE_GI)
    float held;
    float3 gathered = GiIrradianceAt(surface.position + CameraPosition, open, surface.view, held);
    irradiance = lerp(irradiance, gathered, held);

    // Where nothing traced the reflection, the probes' light along it, the world's colour and shade that way, with the
    // sky's own detail kept by the share of the sky the probes see there: in the open the sky as it was, in a courtyard
    // the courtyard. Their light along a direction is irradiance over pi, as the sky's harmonics and cube are read.
    [branch] if (held > 0.0 && traced.a < 1.0)
    {
        float aroundHeld;
        float3 around = GiIrradianceAt(surface.position + CameraPosition, dominant, surface.view, aroundHeld);
        float3 skyAround = ShIrradiance(dominant, sh);
        float seen = saturate(Luminance(around) / max(Luminance(skyAround), 1e-4));
        prefiltered = lerp(prefiltered, max(around + (prefiltered - skyAround) * seen, 0.0), aroundHeld);
        [branch] if (surface.model == ShadingClearCoat)
        {
            coatPrefiltered = lerp(coatPrefiltered, max(around + (coatPrefiltered - skyAround) * seen, 0.0), aroundHeld);
        }
    }
#endif
    prefiltered = lerp(prefiltered, traced.rgb, traced.a);
    float3 bounced = MultiBounceAo(visibility, shading.diffuseColor);
    luminance += ShadeAmbient(shading, irradiance * bounced, prefiltered, coatPrefiltered, 1.0, specularOcclusion);
    return luminance;
}

#endif
