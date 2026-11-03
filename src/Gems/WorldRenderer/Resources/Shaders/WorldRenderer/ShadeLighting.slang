// The GBuffer lit into the HDR picture: the sun through its pages, the lights of the pixel's froxel, the sky's light
// on the surface, and the air in front of it; the sky itself where nothing was drawn, from the sky the entry is
// specialised to. The pass header's x is the frame's constants, y the shadow constants and z the lighting constants.
module "WorldRenderer/ShadeLighting";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Pack;
import Scene.Lights;
import Scene.Lighting;
import Surface.Brdf;
import Light.Lights;
import Shadow.Virtual;
import Sky.ISky;
import Fog.Froxels;

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

        uint froxel = GetFroxelIndex(GetFroxel(frame, thread.xy, viewDepth));
        uint count = GetFroxelLightCount(lighting.froxelLists, froxel);
        for (uint i = 0u; i < count; i++)
        {
            GpuLight light = LoadLight(lighting.lights, GetFroxelLight(lighting.froxelLists, froxel, i));
            LightSample sample = SampleLight(light, world, normal);
            color += Evaluate(brdf, normal, viewDirection, sample.direction) * sample.irradiance;
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
