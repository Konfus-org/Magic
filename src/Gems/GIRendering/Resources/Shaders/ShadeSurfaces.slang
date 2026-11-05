// Every pixel of the picture lit, into the HDR picture: a surface by the sun through the air, by the local lights its
// tile lists and by the sky over it, its direct and indirect light summed over the shared BRDF; and the sky itself
// where nothing was drawn. The pass header's x is the frame's constants and z the lighting constants.
module "ShadeSurfaces";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Pack;
import Scene.Lighting;
import Scene.Lights;
import Light.Lights;
import Sky.ISky;
import Surface.Brdf;

// The light a surface lit by the sky alone reflects, by direction: the sky's irradiance over its hemisphere for the
// diffuse, and the sky along the mirror direction for the specular, giving way to the irradiance as it roughens.
float3 GetSkyLight<S : ISky>(FrameConstants frame, LightingConstants lighting, Brdf brdf, float roughness, float3 normal, float3 view)
{
    float3 irradiance;
    float3 reflected;
    if (lighting.hasSky != 0u)
    {
        irradiance = S.GetAmbient(CreateSkyInputs(frame, lighting, normal, false), normal);
        float3 mirror = reflect(-view, normal);
        reflected = S.Evaluate(CreateSkyInputs(frame, lighting, mirror, false));
    }
    else
    {
        irradiance = lighting.sunIlluminance.rgb * 0.1 * (0.5 + 0.5 * normal.y);
        reflected = irradiance * InvPi;
    }

    float3 fresnel = GetEnvironmentBrdf(brdf.f0, roughness, max(dot(normal, view), 1e-4));
    float3 specular = lerp(reflected, irradiance * InvPi, saturate(roughness));
    return brdf.diffuse * (1.0 - fresnel) * irradiance * InvPi + fresnel * specular;
}

[shader("compute")]
[numthreads(8, 8, 1)]
void ShadeSurfaces<S : ISky>(uint3 thread : SV_DispatchThreadID)
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
    float3 view = NormalizeSafe(-relative, float3(0.0, 0.0, -1.0));
    StorageRef hdr = { lighting.hdr, 0u };

    UintTextureRef visibility = { frame.visibility };
    if (visibility.Load(thread.xy, 0u) == 0u)
    {
        float3 sky = lighting.hasSky != 0u
            ? S.Evaluate(CreateSkyInputs(frame, lighting, -view, true))
            : frame.skyRadiance.rgb * lighting.sunIlluminance.r * lerp(0.6, 1.4, 1.0 - uv.y);
        hdr.Store(thread.xy, float4(sky, 1.0));
        return;
    }

    TextureRef normalTexture = { frame.gbufferNormal };
    TextureRef baseTexture = { frame.gbufferBase };
    TextureRef surfaceTexture = { frame.gbufferSurface };
    float3 normal = DecodeOctahedral(normalTexture.Load(thread.xy, 0u).xy * 2.0 - 1.0);
    float4 base = baseTexture.Load(thread.xy, 0u);
    float4 surface = surfaceTexture.Load(thread.xy, 0u);
    Brdf brdf = CreateBrdf(base.rgb, base.a, surface.x, surface.y);

    float3 toSun = frame.sunDirection.xyz;
    float3 sunIlluminance = lighting.hasSky != 0u
        ? S.GetSunIlluminance(CreateSkyInputs(frame, lighting, toSun, false))
        : lighting.sunIlluminance.rgb;
    float3 color = Evaluate(brdf, normal, view, toSun) * sunIlluminance;

    // The lights of the pixel's tile, a word of bits at a time.
    float3 world = relative + frame.cameraPosition.xyz;
    ArrayRef<uint> tiles = { lighting.lightTiles };
    uint tilesAcross = (size.x + LightTileSize - 1u) / LightTileSize;
    uint offset = GetLightTileOffset(thread.xy / LightTileSize, tilesAcross);
    uint buckets = (min(lighting.lightCount, MaxLights) + 31u) / 32u;
    for (uint bucket = 0u; bucket < buckets; bucket++)
    {
        uint bits = tiles[offset + bucket];
        while (bits != 0u)
        {
            uint bit = firstbitlow(bits);
            bits &= bits - 1u;
            LightSample sample = SampleLight(LoadLight(lighting.lights, bucket * 32u + bit), world, normal);
            color += Evaluate(brdf, normal, view, sample.direction) * sample.irradiance;
        }
    }
    color += GetSkyLight<S>(frame, lighting, brdf, surface.x, normal, view) * surface.z;
    hdr.Store(thread.xy, float4(color, 1.0));
}
