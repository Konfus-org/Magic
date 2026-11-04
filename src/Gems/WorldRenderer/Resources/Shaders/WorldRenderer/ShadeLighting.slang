// The GBuffer lit into the HDR picture: the sun through its pages, the lights of the pixel's froxel, the strongest
// of them shadowed through the tracer the entry is specialised to, the indirect light from the screen probes around the pixel,
// and the air in front of it; the sky itself where nothing was drawn, from the sky the entry is specialised to. The
// pass header's x is the frame's constants, y the shadow constants, z the lighting constants and w the field
// constants.
module "WorldRenderer/ShadeLighting";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Pack;
import Core.Sampling;
import Scene.Lights;
import Scene.Probes;
import Scene.Translucency;
import Scene.Lighting;
import Scene.Field;
import Surface.Brdf;
import Light.Lights;
import Shadow.Virtual;
import Sky.ISky;
import Fog.Froxels;
import Trace.IWorldTracer;
import Trace.DistanceField;

// How many of a pixel's lights are shadowed through the field: the strongest, the rest unshadowed.
static const uint TracedLights = 4u;

// The four screen probes around a pixel on its plane: their irradiance, and their radiance towards a direction;
// false when none lies on the plane.
bool SampleScreenProbes(GiConstants gi, uint2 pixel, float3 world, float3 normal, float viewDepth, float3 direction, out float3 irradiance, out float3 reflected)
{
    irradiance = float3(0.0);
    reflected = float3(0.0);
    float2 tileSpace = ((float2(pixel) + 0.5) / float(ScreenProbeTile)) - 0.5;
    float2 floored = floor(tileSpace);
    float2 fraction = tileSpace - floored;
    int2 base = int2(floored);
    int2 last = int2(gi.screenProbesX, gi.screenProbesY) - 1;
    TextureRef filtered = { gi.screenFiltered };
    ArrayRef<float4> irradianceTable = { gi.screenIrradiance };
    float weightSum = 0.0;
    for (int y = 0; y < 2; y++)
    {
        for (int x = 0; x < 2; x++)
        {
            uint2 tile = uint2(clamp(base + int2(x, y), int2(0), last));
            uint index = GetScreenProbeIndex(gi, tile);
            ScreenProbe probe = LoadScreenProbe(gi.screenProbes, index);
            if ((probe.flags & ScreenProbeFlagValid) == 0u)
                continue;

            float bilinear = (x == 0 ? 1.0 - fraction.x : fraction.x) * (y == 0 ? 1.0 - fraction.y : fraction.y);
            float weight = bilinear * GetProbePlaneWeight(world, normal, viewDepth, probe.position, probe.normal);
            if (weight <= 0.0)
                continue;

            irradiance += irradianceTable[index].rgb * weight;
            float2 texel = GetScreenProbeTexel(CreateBasis(probe.normal), direction);
            reflected += SampleScreenProbe(filtered, GetScreenProbeOrigin(tile), texel) * weight;
            weightSum += weight;
        }
    }

    if (weightSum < 1e-3)
        return false;

    irradiance /= weightSum;
    reflected /= weightSum;
    return true;
}

[shader("compute")]
[numthreads(8, 8, 1)]
void ShadeLighting<S : ISky, T : IWorldTracer>(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    LightingConstants lighting = GetLighting();
    uint2 size = uint2(frame.width, frame.height);
    if (any(thread.xy >= size))
        return;

    float2 uv = GetTexelCenterUv(thread.xy, size);
    TextureRef depthTexture = { frame.depth };
    float depth = depthTexture.Load(thread.xy, 0u).x;
    float3 relative = ReconstructRelative(frame, uv, depth > 0.0 ? depth : 1e-6);
    float3 viewDirection = NormalizeSafe(-relative, float3(0.0, 0.0, -1.0));

    SkyInputs sky;
    sky.viewDirection = -viewDirection;
    sky.sunDirection = frame.sunDirection.xyz;
    sky.sunIlluminance = lighting.sunIlluminance.rgb;
    sky.altitude = lighting.cameraAltitude.x;
    sky.transmittanceLut = lighting.transmittanceLut;
    sky.multiScatteringLut = lighting.multiScatteringLut;
    sky.skyViewLut = lighting.skyViewLut;
    sky.sampler = lighting.lutSampler;
    bool hasSky = lighting.hasSky != 0u;

    float3 color;
    float viewDepth;
    UintTextureRef visibility = { frame.visibility };
    if (visibility.Load(thread.xy, 0u) == 0u)
    {
        viewDepth = FogFar;
        color = hasSky ? S.Evaluate(sky) : frame.skyRadiance.rgb * lighting.sunIlluminance.r * lerp(0.6, 1.4, 1.0 - uv.y);
    }
    else
    {
        viewDepth = ToViewDepth(depth, frame.near);
        TextureRef normalTexture = { frame.gbufferNormal };
        TextureRef baseTexture = { frame.gbufferBase };
        TextureRef surfaceTexture = { frame.gbufferSurface };
        float3 normal = DecodeOctahedral(normalTexture.Load(thread.xy, 0u).xy * 2.0 - 1.0);
        float4 base = baseTexture.Load(thread.xy, 0u);
        float4 surface = surfaceTexture.Load(thread.xy, 0u);
        Brdf brdf = MakeBrdf(base.rgb, base.a, surface.x, surface.y);
        float3 world = relative + frame.cameraPosition.xyz;

        ShadowConstants shadow = LoadShadowConstants(GetPassHeader().y);
        float3 sunIlluminance = hasSky ? S.GetSunIlluminance(sky) : lighting.sunIlluminance.rgb;
        float3 sunVisible = SampleSunShadow(shadow, world, normal, viewDepth) * GetGlassShadow(GetTranslucency(), world);
        color = Evaluate(brdf, normal, viewDirection, frame.sunDirection.xyz) * sunIlluminance * sunVisible;

        // Every light of the froxel, the strongest few kept aside to be shadowed through the field.
        uint froxel = GetFroxelIndex(GetFroxel(frame, thread.xy, viewDepth));
        uint count = GetFroxelLightCount(lighting.froxelLists, froxel);
        float3 tracedLight[TracedLights];
        float3 tracedDirection[TracedLights];
        float tracedDistance[TracedLights];
        float tracedCone[TracedLights];
        float tracedStrength[TracedLights];
        for (uint t = 0u; t < TracedLights; t++)
        {
            tracedLight[t] = float3(0.0);
            tracedDirection[t] = float3(0.0, 1.0, 0.0);
            tracedDistance[t] = 0.0;
            tracedCone[t] = 0.0;
            tracedStrength[t] = -1.0;
        }

        for (uint i = 0u; i < count; i++)
        {
            GpuLight light = LoadLight(lighting.lights, GetFroxelLight(lighting.froxelLists, froxel, i));
            LightSample sample = SampleLight(light, world, normal);
            float3 lit = Evaluate(brdf, normal, viewDirection, sample.direction) * sample.irradiance;
            float strength = dot(lit, float3(0.2126, 0.7152, 0.0722));
            uint weakest = 0u;
            for (uint t = 1u; t < TracedLights; t++)
            {
                if (tracedStrength[t] < tracedStrength[weakest])
                    weakest = t;
            }

            if (strength > tracedStrength[weakest])
            {
                color += tracedStrength[weakest] > 0.0 ? tracedLight[weakest] : float3(0.0);
                tracedLight[weakest] = lit;
                tracedDirection[weakest] = sample.direction;
                tracedDistance[weakest] = distance(light.position, world);
                tracedCone[weakest] = max(light.radius, 0.05) / max(tracedDistance[weakest], 0.1);
                tracedStrength[weakest] = strength;
            }
            else
            {
                color += lit;
            }
        }

        uint fieldLevel;
        FieldConstants fieldConstants = GetFieldConstants();
        SampleField(fieldConstants, world, fieldLevel);
        float start = fieldConstants.voxelSizes[fieldLevel] * 1.5;
        for (uint t = 0u; t < TracedLights; t++)
        {
            if (tracedStrength[t] <= 0.0)
                continue;

            Ray ray = { world + normal * start, tracedDirection[t] };
            TraceLimits limits = { start, max(tracedDistance[t] - start, start), tracedCone[t] };
            TraceHit hit = T.Trace(ray, limits);
            color += tracedLight[t] * hit.visibility;
        }

        // The indirect light: the screen probes around the pixel, else the world probes, else the sky; the diffuse
        // from their irradiance, the specular from their radiance towards the reflection, the rougher the surface
        // the more of the irradiance instead; and a short cone up the normal through the field for what the probes
        // cannot keep apart at a contact.
        GiConstants gi = LoadConstants<GiConstants>(lighting.gi);
        float3 reflection = reflect(-viewDirection, normal);
        float3 irradiance;
        float3 reflected;
        if (!SampleScreenProbes(gi, thread.xy, world, normal, viewDepth, reflection, irradiance, reflected))
        {
            if (!SampleWorldProbeIrradiance(gi, world + normal * start, normal, irradiance))
                irradiance = hasSky ? S.GetAmbient(sky, normal) : lighting.sunIlluminance.rgb * 0.1 * (0.5 + 0.5 * normal.y);

            reflected = irradiance * InvPi;
        }

        Ray coneRay = { world + normal * start, normal };
        TraceLimits coneLimits = { start, 1.0 + start, 0.5 };
        float occlusion = lerp(0.4, 1.0, DistanceFieldTracer.Trace(coneRay, coneLimits).visibility);
        float nDotV = max(dot(normal, viewDirection), 1e-4);
        float3 fresnel = brdf.f0 + (max(float3(1.0 - brdf.alpha), brdf.f0) - brdf.f0) * Pow5(1.0 - nDotV);
        // The sharp reflections below the limit, the probes' radiance above it, the irradiance when rough.
        TextureRef reflections = { gi.reflections };
        float sharpness = gi.hasReflections != 0u ? 1.0 - smoothstep(ReflectionRoughnessSharp, ReflectionRoughnessLimit, surface.x) : 0.0;
        float3 reflectedSharp = reflections.Load(thread.xy, 0u).rgb;
        float3 specular = lerp(lerp(reflected, irradiance * InvPi, saturate(surface.x * 2.0 - 0.5)), reflectedSharp, sharpness);
        float3 indirect = (brdf.diffuse * (1.0 - fresnel) * irradiance * InvPi + fresnel * (1.0 - brdf.alpha * 0.5) * specular) * (surface.z * occlusion);
        if (lighting.debugView == 3u)
        {
            color = indirect;
        }
        else if (lighting.debugView == 4u)
        {
            float3 probes;
            color = SampleWorldProbeIrradiance(gi, world + normal * start, normal, probes) ? probes * InvPi : float3(1.0, 0.0, 1.0);
        }
        else if (lighting.debugView == 5u)
        {
            color = reflectedSharp;
        }
        else if (lighting.debugView == 6u)
        {
            color = GetGlassShadow(GetTranslucency(), world);
        }
        else if (lighting.debugView == 7u)
        {
            // How lit by the sun, through its pages alone; magenta where no level had a page, and the level that
            // answered as a tint of blue, the deeper the coarser.
            uint levelUsed;
            float sun = SampleSunShadow(shadow, world, normal, viewDepth, levelUsed);
            color = levelUsed >= shadow.levels ? float3(1.0, 0.0, 1.0) : lerp(float3(sun), float3(0.0, 0.0, 1.0), float(levelUsed) / float(shadow.levels));
        }
        else
        {
            color += indirect;
        }
    }

    // The shadow views are shown as they are, with no air before them.
    if (lighting.fog.isPresent != 0u && lighting.debugView < 6u)
    {
        VolumeRef integrated = { lighting.fogIntegrated };
        SamplerRef sampler = { lighting.fogSampler };
        float4 air = integrated.SampleLevel(sampler, GetFogUvw(frame, uv, viewDepth), 0.0);
        color = color * air.w + air.rgb;
    }

    StorageRef hdr = { lighting.hdr, 0u };
    hdr.Store(thread.xy, float4(color, 1.0));
}
