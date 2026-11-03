// The GBuffer lit into the HDR picture: the sun through its pages, the lights of the pixel's froxel, the strongest
// of them shadowed by cones through the distance field, the sky's light on the surface, and the air in front of it;
// the sky itself where nothing was drawn, from the sky the entry is specialised to. The pass header's x is the
// frame's constants, y the shadow constants, z the lighting constants and w the field constants.
module "WorldRenderer/ShadeLighting";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Pack;
import Scene.Lights;
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

[shader("compute")]
[numthreads(8, 8, 1)]
void ShadeLighting<S : ISky>(uint3 thread : SV_DispatchThreadID)
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
        float visible = SampleSunShadow(shadow, world, normal, viewDepth);
        color = Evaluate(brdf, normal, viewDirection, frame.sunDirection.xyz) * sunIlluminance * visible;

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
            TraceHit hit = DistanceFieldTracer.Trace(ray, limits);
            color += tracedLight[t] * hit.visibility;
        }

        float3 ambient = hasSky ? S.GetAmbient(sky, normal) : lighting.sunIlluminance.rgb * 0.1 * (0.5 + 0.5 * normal.y);
        color += EvaluateAmbient(brdf, normal, viewDirection) * ambient * surface.z;
    }

    if (lighting.fog.isPresent != 0u)
    {
        VolumeRef integrated = { lighting.fogIntegrated };
        SamplerRef sampler = { lighting.fogSampler };
        float4 air = integrated.SampleLevel(sampler, GetFogUvw(frame, uv, viewDepth), 0.0);
        color = color * air.w + air.rgb;
    }

    StorageRef hdr = { lighting.hdr, 0u };
    hdr.Store(thread.xy, float4(color, 1.0));
}
