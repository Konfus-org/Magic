// The air lit and summed: each froxel scatters the sun, shadowed by its pages, the lights of its froxel and the sky
// towards the camera; then each column is summed from the camera out, so a pixel reads what the air in front of it
// adds and keeps. The pass header's x is the frame's constants, y the shadow constants and z the lighting constants.
module "WorldRenderer/Fog";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Scene.Lights;
import Scene.Lighting;
import Light.Lights;
import Shadow.Virtual;
import Sky.ISky;
import Fog.Froxels;

[shader("compute")]
[numthreads(4, 4, 4)]
void FogInject<S : ISky>(uint3 thread : SV_DispatchThreadID)
{
    if (any(thread >= FogGrid))
        return;

    FrameConstants frame = GetFrame();
    LightingConstants lighting = GetLighting();
    StorageVolumeRef target = { lighting.fogScattering, 0u };
    float3 relative = GetFroxelCenter(frame, thread);
    float3 world = relative + frame.cameraPosition.xyz;
    float density = GetFogDensity(lighting.fog, world.y);
    if (density < 1e-6)
    {
        target.Store(thread, float4(0.0));
        return;
    }

    float3 view = NormalizeSafe(-relative, float3(0.0, 0.0, -1.0));
    float3 scattering = lighting.fog.albedo * density;
    SkyInputs sky;
    sky.viewDirection = view;
    sky.sunDirection = frame.sunDirection.xyz;
    sky.sunIlluminance = lighting.sunIlluminance.rgb;
    sky.altitude = lighting.cameraAltitude.x;
    sky.transmittanceLut = lighting.transmittanceLut;
    sky.multiScatteringLut = lighting.multiScatteringLut;
    sky.skyViewLut = lighting.skyViewLut;
    sky.sampler = lighting.lutSampler;
    float3 sunIlluminance = lighting.hasSky != 0u ? S.GetSunIlluminance(sky) : lighting.sunIlluminance.rgb;
    float3 ambient = lighting.hasSky != 0u ? S.GetAmbient(sky, float3(0.0, 1.0, 0.0)) : lighting.sunIlluminance.rgb * 0.1;

    ShadowConstants shadow = LoadShadowConstants(GetPassHeader().y);
    float viewDepth = dot(relative, float3(frame.view[0][2], frame.view[1][2], frame.view[2][2]));
    float visible = SampleSunShadow(shadow, world, frame.sunDirection.xyz, viewDepth);
    // The phase takes the cosine between the way the light travels and the way it is scattered, towards the camera.
    float3 light = sunIlluminance * visible * PhaseHenyeyGreenstein(-dot(view, frame.sunDirection.xyz), lighting.fog.anisotropy);
    light += ambient * (0.25 * InvPi);

    uint2 pixel = uint2(float2(thread.xy) / float2(FogGrid.xy) * float2(frame.width, frame.height));
    uint froxel = GetFroxelIndex(GetFroxel(frame, pixel, viewDepth));
    uint count = GetFroxelLightCount(lighting.froxelLists, froxel);
    for (uint i = 0u; i < count; i++)
    {
        GpuLight local = LoadLight(lighting.lights, GetFroxelLight(lighting.froxelLists, froxel, i));
        LightSample sample = SampleLight(local, world, view);
        light += sample.irradiance * PhaseHenyeyGreenstein(-dot(view, sample.direction), lighting.fog.anisotropy);
    }

    target.Store(thread, float4(scattering * light, density));
}

[shader("compute")]
[numthreads(8, 8, 1)]
void FogIntegrate(uint3 thread : SV_DispatchThreadID)
{
    if (any(thread.xy >= FogGrid.xy))
        return;

    LightingConstants lighting = GetLighting();
    VolumeRef source = { lighting.fogScattering };
    StorageVolumeRef target = { lighting.fogIntegrated, 0u };
    float3 inscatter = float3(0.0);
    float transmittance = 1.0;
    for (uint z = 0u; z < FogGrid.z; z++)
    {
        float4 froxel = source.Load(uint3(thread.xy, z), 0u);
        float depth = GetFogDepth(float(z + 1u) / float(FogGrid.z)) - GetFogDepth(float(z) / float(FogGrid.z));
        float extinction = max(froxel.w, Epsilon);
        float through = exp(-froxel.w * depth);
        inscatter += transmittance * froxel.rgb * ((1.0 - through) / extinction);
        transmittance *= through;
        target.Store(uint3(thread.xy, z), float4(inscatter, transmittance));
    }
}
