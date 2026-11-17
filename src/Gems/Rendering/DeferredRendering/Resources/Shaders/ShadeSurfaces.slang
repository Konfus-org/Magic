// Every pixel of the picture lit, into the HDR picture: a surface by the sun through the air, by the local lights its
// tile lists, each dimmed by the shadow mask, the sun also by the clouds and coloured by its tint through glass, and by
// the light around it, traced on a GPU that traces rays and else the sky's, and what it reflects, traced where it is
// smooth enough, its direct and indirect light summed over the shared BRDF and added to its own emissive light, which
// the material resolve left in the picture. Where nothing was drawn, it is the sky itself, with its clouds, or the
// failure of the sky or of its clouds where they failed. The pass header's x is the frame's buffer, which holds the
// lights, the shadows, the indirect light, whose ids are 0 where nothing is traced, and the air. Its y holds the
// failure bits.
module "ShadeSurfaces";

import Core.Color;
import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Scene.Lights;
import Scene.Shadows;
import Light.Air;
import Light.Indirect;
import Light.LocalLights;
import Light.Probes;
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
// PROTO: how much of the light a shadowed surface would get from each light unshadowed it still reflects, from every
// side, in place of that light's bounce off the surroundings where the indirect light gives less.
static const float ShadowFill = 0.05;

// PROTO: the shares of the sky a surface sees between which the sun's fill rises from none to all of it: under the
// first, a room that sees the sky only through a window gets none, and a wall in the open, which sees half, gets all.
static const float2 FillSkyShare = float2(0.1, 0.4);

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
// near the surface and the probes' beyond, giving way to the probes' own where the traced pixels around it saw none,
// such as a thin leaf against the sky, else the sky's over its hemisphere. The specular is the traced reflections
// as far as they cover the pixel, where the surface is smooth enough for them, less as it roughens to their limit.
// Elsewhere, where a sky entity draws the sky, it is the sky along the mirror direction, giving way to the diffuse as
// the surface roughens. Where the diffuse is traced, or the sky is the open sky, it is the diffuse alone, so a closed
// room reflects none of the sky. The nearby surfaces in the depth hide part of both where surfaces meet, the specular
// as much as that occlusion hides of its lobe (Lagarde and de Rousiers, "Moving Frostbite to Physically Based
// Rendering", 2014). The specular rises towards edge-on only where the surface is smooth enough to trace, less as it
// roughens to that limit. The diffuse is never darker than the fills, what a white diffuse surface reflects of the
// stand-in bounce of the sun and of the local lights, so a shadow is never black: it gets the part of the fills its own
// brightness lacks, so light that is bright enough keeps its colour. Where the diffuse is traced, the sun fills only
// where the surface sees enough of the sky, so it fills no room.
float3 ShadeIndirectLight<S : ISky>(
    FrameConstants frame,
    Lights lights,
    Brdf brdf,
    float roughness,
    uint2 pixel,
    float3 relative,
    float3 normal,
    float3 view,
    float3 sunFill,
    float3 localFill
)
{
    IndirectLight indirect = GetIndirectLight();
    SkyTables tables = GetSkyTables();
    bool isTraced = indirect.diffuse != 0u;
    float3 diffuse;
    if (isTraced)
    {
        float4 traced = SampleIndirectDiffuse(frame, indirect.diffuse, pixel, relative, normal);
        float3 probes = GetProbeIrradiance(indirect.grid, relative + frame.cameraPosition.xyz, normal);
        diffuse = (indirect.termsOff & 8u) == 0u ? lerp(probes, traced.rgb, traced.a) : traced.rgb;
        float skyShare = SampleSkyShare(frame, indirect.skyShare, pixel, relative, normal);
        sunFill *= smoothstep(FillSkyShare.x, FillSkyShare.y, skyShare);
    }
    else
        diffuse = S.GetAmbient(CreateSkyInputs(lights, tables, normal, false), normal) * InvPi;

    float3 specular = diffuse;
    if (!isTraced && HasSky(tables))
    {
        float3 reflected = S.Evaluate(CreateSkyInputs(lights, tables, reflect(-view, normal), false));
        specular = lerp(reflected, diffuse, saturate(roughness));
    }

    float smooth = saturate((TracedRoughness - roughness) / TracedRoughnessFade);
    if (indirect.reflections != 0u && roughness < TracedRoughness)
    {
        float4 traced = SampleIndirectReflections(frame, indirect.reflections, pixel, relative, normal, roughness);
        specular = lerp(specular, traced.rgb, traced.a * smooth);
    }

    float3 fill = (indirect.termsOff & 4u) == 0u ? sunFill + localFill : float3(0.0);
    diffuse += fill * saturate(1.0 - GetLuminance(diffuse) / max(GetLuminance(fill), Epsilon));
    float occlusion = GetIndirectOcclusion(frame, pixel, relative, normal);
    float facing = max(dot(normal, view), 1e-4);
    float specularOcclusion = saturate(pow(facing + occlusion, exp2(-16.0 * roughness - 1.0)) - 1.0 + occlusion);

    // A surface too rough to trace reflects as one seen face-on does, with no rise of its specular towards edge-on:
    // the rise, untinted, covers the colour of a dark side seen along its face with a pale sheen.
    float3 fresnel = GetEnvironmentBrdf(brdf.f0, roughness, lerp(1.0, facing, smooth));
    return occlusion * brdf.diffuse * (1.0 - fresnel) * diffuse + specularOcclusion * fresnel * specular;
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
        SkyInputs seen = CreateSkyInputs(lights, GetSkyTables(), -view, true);
        seen.screenY = uv.y;
        float3 color = ApplyClouds(S.Evaluate(seen), -view);
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
    float3 sunUnshadowed = GetSunIlluminance<S>(lights) * GetCloudShadow(GetAir(), world, toSun, frame.timeSec);
    float3 sunLight = sunUnshadowed * GetShadow(shadowWords, 0u) * LoadSunTint(shadows.sunTint, thread.xy);
    float3 color = Evaluate(brdf, normal, view, toSun) * sunLight;

    // The lights of the pixel's tile, a word of bits at a time.
    ArrayRef<uint> tiles = { lights.tiles };
    uint offset = GetLightTileOffset(thread.xy / LightTileSize, size.x);
    uint buckets = (min(lights.count, MaxLights) + 31u) / 32u;
    uint slot = 1u;
    float3 localUnshadowed = float3(0.0);
    for (uint bucket = 0u; bucket < buckets; bucket++)
    {
        uint bits = tiles[offset + bucket];
        while (bits != 0u)
        {
            uint bit = firstbitlow(bits);
            bits &= bits - 1u;
            LightSample sample = SampleLight(LoadLight(lights.table, bucket * 32u + bit), world, normal);
            color += Evaluate(brdf, normal, view, sample.direction) * sample.irradiance * GetShadow(shadowWords, slot);
            localUnshadowed += sample.irradiance;
            slot++;
        }
    }

    float fill = ShadowFill * InvPi;
    color += hdr.Load(thread.xy).rgb + surface.z * ShadeIndirectLight<S>(
        frame, lights, brdf, surface.x, thread.xy, relative, normal, view, sunUnshadowed * fill, localUnshadowed * fill);
    hdr.Store(thread.xy, float4(color, 1.0));
}
