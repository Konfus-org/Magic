// Every pixel of the picture lit, into the HDR picture: a surface by the sun through the air, by the local lights its
// tile lists, each dimmed by the shadow mask, the sun also by the clouds and coloured by its tint through glass, and by
// the light around it, traced on a GPU that traces rays and else the sky's, and what it reflects, traced where it is
// smooth enough, its direct and indirect light summed over the shared BRDF and added to its own emissive light, which
// the material resolve left in the picture. Where nothing was drawn, it is the sky itself, with its clouds, or the
// failure of the sky or of its clouds where they failed. The pass header's x is the frame's buffer, which holds the
// lights, the shadows, the indirect light, whose ids are 0 where nothing is traced, and the air. Its y holds the
// failure bits.
module "ShadeSurfaces";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Scene.Lights;
import Scene.Shadows;
import Light.Air;
import Light.Indirect;
import Light.LocalLights;
import Scene.Failures;
import Sky.ISky;
import Surface.Brdf;

// The pass header's y, C#'s twin: the bit set while the sky failed, and the one set while its clouds failed.
static const uint SkyFailed = 1u;
static const uint WeatherFailed = 2u;

// The colours of the failures, linear, from the failure mockup's sRGB colours: coral #D85A30, green #639922. Their
// flash, their glow and their text's sizes are Scene/Failures'.
static const float3 Coral = float3(0.687, 0.102, 0.030);
static const float3 Green = float3(0.125, 0.319, 0.016);

// The band of the sky where clouds draw, as the sine of the elevation, from its bottom to its top.
static const float2 CloudBand = float2(0.05, 0.35);

// The texts, as character codes: "Sky failed" and "Weather failed".
static const uint SkyText[10] = { 83u, 107u, 121u, 32u, 102u, 97u, 105u, 108u, 101u, 100u };
static const uint WeatherText[14] = { 87u, 101u, 97u, 116u, 104u, 101u, 114u, 32u, 102u, 97u, 105u, 108u, 101u, 100u };

// Whether a pixel lies on a dot of the text, which repeats across the screen in rows, with a gap after each copy.
bool IsFailureTextPixel(uint2 pixel, bool isWeather)
{
    uint length = isWeather ? 14u : 10u;
    uint2 dot = pixel / uint(FailureDotPixels);
    uint index = (dot.x / FailureCellWidth) % (length + FailureCopyGap);
    if (index >= length)
        return false;

    uint code = isWeather ? WeatherText[index] : SkyText[index];
    return IsGlyphDot(code, uint2(dot.x % FailureCellWidth, dot.y % FailureRowHeight));
}

// The failure at a pixel where the sky or its clouds would draw: the band of the clouds in flashing green while they
// failed, else the whole sky in flashing coral, with its text over it, white with a black shadow a pixel down and
// right.
float3 ShowSkyFailure(bool isWeather, uint2 pixel, float timeSec)
{
    if (IsFailureTextPixel(pixel, isWeather))
        return float3(FailureTextGlow);

    if (pixel.x > 0u && pixel.y > 0u && IsFailureTextPixel(pixel - 1u, isWeather))
        return float3(0.0);

    return (isWeather ? Green : Coral) * GetFailurePulse(timeSec) * FailureColorGlow;
}

// The light a surface at a pixel reflects of what reaches it indirectly. The diffuse is what a white diffuse surface
// reflects, its irradiance over pi: the traced light where it was traced, which sees what shades and bounces the light
// near the surface and the probes' beyond, else the open sky's over its hemisphere. The specular is the traced
// reflections as far as they cover the pixel, where the surface is smooth enough for them. Elsewhere it is the sky
// along the mirror direction, giving way to the diffuse as the surface roughens. Where the diffuse is traced it is the
// diffuse alone, so a closed room reflects none of the sky.
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
        float3 color = ApplyClouds(sky, -view);
        uint failed = GetPassHeader().y;
        bool isWeather = (failed & WeatherFailed) != 0u && -view.y >= CloudBand.x && -view.y <= CloudBand.y;
        if (isWeather || (failed & SkyFailed) != 0u)
        {
            color = ShowSkyFailure(isWeather, thread.xy, frame.timeSec);

            // The text is fixed to the screen, so the pixel does not move, and the upscaling's history keeps it sharp.
            StorageRef velocity = { frame.velocity, 0u };
            velocity.Store(thread.xy, float4(0.0, 0.0, 0.0, 0.0));
        }

        hdr.Store(thread.xy, float4(color, 1.0));
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
