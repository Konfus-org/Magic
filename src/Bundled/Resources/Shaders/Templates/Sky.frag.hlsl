// The sky template: one triangle over what nothing was drawn on (depth tested against the view's), or over a face of a
// cube when it captures the sky (SKY_CUBE) for the ambient light. The world's sky is its material's sky shader
// (Include/Sky.hlsli), composed before this, evaluated along each direction from the frame's sun, moon and weather;
// what it gives is the sky's luminance in nits, not pre-exposed, so it lights the world as the sky would. The sun's and
// the moon's discs it hands back apart are shown in the view and left out of the capture, which their own light stands
// for. Without a sky (SKY_FALLBACK) it is the renderer's own: a clear sky as bright as the sun makes it, with the sun's
// disc. What a
// sky gives off pre-exposed is added in the view as it is, and never captured. In the view each pixel's dither moves
// each frame, interleaved gradient noise, for a sky that integrates by steps; a capture's is the same everywhere.

#include "Include/Color.hlsli"
#include "Include/Cube.hlsli"
#include "Include/Fullscreen.hlsli"
#include "Include/Math.hlsli"
#include "Include/Noise.hlsli"
#include "Include/Sky.hlsli"
#include "Include/SkyFrame.hlsli"

// The fallback sky's colours, and its luminance per lux of the sun: a clear sky's zenith is around 0.03 to 0.08 of the
// sun's illuminance, in nits.
static const float3 Zenith = float3(0.25, 0.45, 0.95);
static const float3 Horizon = float3(0.75, 0.85, 1.0);
static const float3 Ground = float3(0.11, 0.105, 0.1);
static const float3 Glow = float3(1.0, 0.85, 0.6);
static const float NitsPerLux = 0.06;

// Nits, when there is no sun to scale it.
static const float NightSky = 0.002;

SkyLight FallbackSky(float3 direction)
{
    float3 towardSun = -SkyLights.SunDirection;
    float sunLux = Luminance(SkyLights.SunIlluminance);
    float day = saturate(towardSun.y * 4.0 + 0.2);
    float up = direction.y;
    float3 sky = up >= 0.0 ? lerp(Horizon, Zenith, sqrt(saturate(up))) : lerp(Horizon * 0.6, Ground, saturate(-up * 4.0));
    sky += Glow * (0.6 * pow(saturate(dot(direction, towardSun)), 8.0));
    float radius = max(SkyLights.SunAngularRadius, 1e-4);
    float disc = dot(direction, towardSun) >= cos(radius) && towardSun.y > 0.0 ? 1.0 : 0.0;

    SkyLight light;
    light.nits = sky * max(sunLux * NitsPerLux * day, NightSky);
    light.discs = SkyLights.SunIlluminance * (disc / (Pi * Pow2(radius)));
    light.emissive = float3(0.0, 0.0, 0.0);
    return light;
}

// The direction a point of the target looks along.
float3 SkyDirection(float2 uv)
{
#if SKY_CUBE
    return CubeDirection(PassIteration(), uv);
#else
    return normalize(ViewToWorld(ViewPosition(uv * ViewSize, 1.0)));
#endif
}

float4 main(PassVaryings input) : SV_Target0
{
    float3 direction = SkyDirection(input.uv);
#if SKY_FALLBACK
    SkyLight light = FallbackSky(direction);
#else
#if SKY_CUBE
    float dither = 0.5;
#else
    float dither = InterleavedGradientNoise(input.position.xy, FrameIndex);
#endif
    SkyLight light = EvaluateSky(SkyInputsOf(direction, SKY_CUBE != 0, dither), LoadMaterialParams(SkyMaterial));
#endif

#if SKY_CUBE
    return float4(light.nits, 1.0);
#else
    return float4((light.nits + light.discs) * LOAD(Exposure, float4, 0u).x + light.emissive, 1.0);
#endif
}
