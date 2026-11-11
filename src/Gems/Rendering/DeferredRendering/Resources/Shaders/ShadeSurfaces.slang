// Every pixel of the picture lit, into the HDR picture: a surface by the sun through the air, by the local lights its
// tile lists, each dimmed by the shadow mask, the sun also by the clouds and coloured by its tint through glass, and by
// the light around it, traced on a GPU that traces rays and else the sky's, and what it reflects, traced where it is
// smooth enough, its direct and indirect light summed over the shared BRDF and added to its own emissive light, which
// the material resolve left in the picture; and the sky itself, with its clouds, where nothing was drawn. The pass
// header's x is the frame's buffer, which holds the lights, the shadows, the GI constants, whose light ids are 0 where
// nothing is traced, and the air.
module "ShadeSurfaces";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Scene.Lights;
import Scene.Shadows;
import Light.Air;
import Light.Indirect;
import Light.Lights;
import Sky.ISky;
import Surface.Brdf;

// The light a surface at a pixel reflects of what reaches it indirectly. The diffuse is what a white diffuse surface
// reflects, its irradiance over pi: the traced light where it was traced, which sees what shades and bounces the light
// near the surface and the probes' beyond; else the open sky's over its hemisphere. The specular is the traced
// reflections as far as they cover the pixel, where the surface is smooth enough for them. Elsewhere it is the sky along
// the mirror direction, giving way to the diffuse as the surface roughens; where the diffuse is traced it is the diffuse
// alone, so a closed room reflects none of the sky.
float3 ShadeIndirectLight<S : ISky>(
    FrameConstants frame,
    Lights lights,
    Brdf brdf,
    float roughness,
    uint2 pixel,
    float3 relative,
    float3 normal,
    float3 view
)
{
    IndirectLight indirect = GetIndirectLight();
    bool isTraced = indirect.diffuse != 0u;
    float3 diffuse;
    if (isTraced)
        diffuse = SampleIndirectDiffuse(frame, indirect.diffuse, pixel, relative, normal);
    else if (lights.hasSky != 0u)
        diffuse = S.GetAmbient(CreateSkyInputs(lights, normal, false), normal) * InvPi;
    else
        diffuse = lights.sunIlluminance.rgb * 0.1 * (0.5 + 0.5 * normal.y) * InvPi;

    float3 specular = diffuse;
    if (!isTraced && lights.hasSky != 0u)
    {
        float3 reflected = S.Evaluate(CreateSkyInputs(lights, reflect(-view, normal), false));
        specular = lerp(reflected, diffuse, saturate(roughness));
    }

    if (indirect.reflections != 0u && roughness <= TracedRoughness)
    {
        float4 traced = SampleIndirectReflections(frame, indirect.reflections, pixel, relative, normal, roughness);
        specular = lerp(specular, traced.rgb, traced.a);
    }

    float3 fresnel = GetEnvironmentBrdf(brdf.f0, roughness, max(dot(normal, view), 1e-4));
    return brdf.diffuse * (1.0 - fresnel) * diffuse + fresnel * specular;
}

[shader("compute")]
[numthreads(8, 8, 1)]
void ShadeSurfaces<S : ISky>(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    Lights lights = GetLights();
    uint2 size = uint2(frame.width, frame.height);
    if (any(thread.xy >= size))
        return;

    float2 uv = GetTexelCenterUv(thread.xy, size);
    TextureRef depthTexture = { frame.depth };
    float depth = depthTexture.Load(thread.xy, 0u).x;
    float3 relative = ReconstructRelative(frame, uv, max(depth, FarDepth));
    float3 view = NormalizeSafe(-relative, float3(0.0, 0.0, -1.0));
    StorageRef hdr = { frame.hdr, 0u };

    // Nothing drawn here leaves the depth as cleared, at the far plane's 0: the sky shows.
    if (depth == 0.0)
    {
        float3 sky = lights.hasSky != 0u
            ? S.Evaluate(CreateSkyInputs(lights, -view, true))
            : GetOpenSkyRadiance(lights) * lerp(0.6, 1.4, 1.0 - uv.y);
        hdr.Store(thread.xy, float4(ApplyClouds(sky, -view), 1.0));
        return;
    }

    TextureRef baseTexture = { frame.gbufferBase };
    TextureRef surfaceTexture = { frame.gbufferSurface };
    float3 normal = LoadNormal(frame.gbufferNormal, thread.xy);
    float4 base = baseTexture.Load(thread.xy, 0u);
    float4 surface = surfaceTexture.Load(thread.xy, 0u);
    Brdf brdf = CreateBrdf(base.rgb, base.a, surface.x, surface.y);

    float3 toSun = lights.sunDirection.xyz;
    Shadows shadows = GetShadows();
    uint4 shadowWords = LoadShadowMask(shadows.mask, thread.xy, size.x);
    float3 world = relative + frame.cameraPosition.xyz;
    float3 sunLight = GetSunIlluminance<S>(lights) * GetShadow(shadowWords, 0u) * LoadSunTint(shadows.sunTint, thread.xy)
        * GetCloudShadow(GetAir(), world, toSun, frame.timeSec);
    float3 color = Evaluate(brdf, normal, view, toSun) * sunLight;

    // The lights of the pixel's tile, a word of bits at a time.
    ArrayRef<uint> tiles = { lights.tiles };
    uint offset = GetLightTileOffset(thread.xy / LightTileSize, size.x);
    uint buckets = (min(lights.count, MaxLights) + 31u) / 32u;
    uint slot = 1u;
    for (uint bucket = 0u; bucket < buckets; bucket++)
    {
        uint bits = tiles[offset + bucket];
        while (bits != 0u)
        {
            uint bit = firstbitlow(bits);
            bits &= bits - 1u;
            LightSample sample = SampleLight(LoadLight(lights.table, bucket * 32u + bit), world, normal);
            color += Evaluate(brdf, normal, view, sample.direction) * sample.irradiance * GetShadow(shadowWords, slot);
            slot++;
        }
    }

    color += ShadeIndirectLight<S>(frame, lights, brdf, surface.x, thread.xy, relative, normal, view) * surface.z
        + hdr.Load(thread.xy).rgb;
    hdr.Store(thread.xy, float4(color, 1.0));
}
