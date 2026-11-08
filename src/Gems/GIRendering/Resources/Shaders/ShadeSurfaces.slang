// Every pixel of the picture lit, into the HDR picture: a surface by the sun through the air, by the local lights its
// tile lists, each dimmed by the shadow mask, and by the light around it, traced on a GPU that traces rays and else the
// sky's, its direct and indirect light summed over the shared BRDF; and the sky itself where nothing was drawn. The pass
// header's x is the frame's constants, z the lighting constants and w the traced diffuse constants, or 0 where there are
// none.
module "ShadeSurfaces";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Pack;
import Scene.Lighting;
import Scene.Lights;
import Scene.Shadows;
import Scene.TracedDiffuse;
import Light.Lights;
import Sky.ISky;
import Surface.Brdf;

// The light a surface at a pixel reflects of what reaches it indirectly. The diffuse is what a white diffuse surface
// reflects, its irradiance over pi: the traced light where it was traced, which sees what shades and bounces the light
// near the surface and the probes' beyond; else the open sky's over its hemisphere. The specular is the sky along the mirror direction, giving way to the diffuse as it roughens.
float3 GetIndirectLight<S : ISky>(
    FrameConstants frame,
    LightingConstants lighting,
    Brdf brdf,
    float roughness,
    uint2 pixel,
    float3 relative,
    float3 normal,
    float3 view
)
{
    float3 diffuse;
    if (GetPassHeader().w != 0u)
        diffuse = SampleTracedDiffuse(frame, GetTracedDiffuse(), pixel, relative, normal);
    else if (lighting.hasSky != 0u)
        diffuse = S.GetAmbient(CreateSkyInputs(frame, lighting, normal, false), normal) * InvPi;
    else
        diffuse = lighting.sunIlluminance.rgb * 0.1 * (0.5 + 0.5 * normal.y) * InvPi;

    float3 reflected = lighting.hasSky != 0u
        ? S.Evaluate(CreateSkyInputs(frame, lighting, reflect(-view, normal), false))
        : diffuse;
    float3 fresnel = GetEnvironmentBrdf(brdf.f0, roughness, max(dot(normal, view), 1e-4));
    float3 specular = lerp(reflected, diffuse, saturate(roughness));
    return brdf.diffuse * (1.0 - fresnel) * diffuse + fresnel * specular;
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
    uint4 shadows = LoadShadowMask(lighting.shadowMask, thread.xy, size.x);
    float3 color = Evaluate(brdf, normal, view, toSun) * sunIlluminance * GetShadow(shadows, 0u);

    // The lights of the pixel's tile, a word of bits at a time.
    float3 world = relative + frame.cameraPosition.xyz;
    ArrayRef<uint> tiles = { lighting.lightTiles };
    uint tilesAcross = (size.x + LightTileSize - 1u) / LightTileSize;
    uint offset = GetLightTileOffset(thread.xy / LightTileSize, tilesAcross);
    uint buckets = (min(lighting.lightCount, MaxLights) + 31u) / 32u;
    uint slot = 1u;
    for (uint bucket = 0u; bucket < buckets; bucket++)
    {
        uint bits = tiles[offset + bucket];
        while (bits != 0u)
        {
            uint bit = firstbitlow(bits);
            bits &= bits - 1u;
            LightSample sample = SampleLight(LoadLight(lighting.lights, bucket * 32u + bit), world, normal);
            color += Evaluate(brdf, normal, view, sample.direction) * sample.irradiance * GetShadow(shadows, slot);
            slot++;
        }
    }
    color += GetIndirectLight<S>(frame, lighting, brdf, surface.x, thread.xy, relative, normal, view) * surface.z;
    hdr.Store(thread.xy, float4(color, 1.0));
}
