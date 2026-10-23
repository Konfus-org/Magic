// How this renderer binds a sky written against Core's Include/Sky.hlsli: composed ahead of the sky, it declares the
// tables the sky's generated LoadMaterialParams reads and how a texture is read (Textures.hlsli), hands the sky the
// view's sun, moon and weather (SkyInputsOf), and, for a world with no sky (SKY_FALLBACK), is the renderer's own: a
// clear sky as bright as the sun makes it, with the sun's disc. The pass composed after the sky reads it along a
// direction by SkyAlong (SkyAlong.hlsli).

#ifndef MAGIC_RENDERING_SKIES_HLSLI
#define MAGIC_RENDERING_SKIES_HLSLI

#include "Include/Color.hlsli"
#include "Include/Sky.hlsli"
#include "Rendering/Include/Tables.hlsli"
#include "Rendering/Include/Textures.hlsli"

// The sky's inputs along a direction; a capture is drawn coarser. dither is the pixel's (Include/Sky.hlsli).
SkyInputs SkyInputsOf(float3 direction, bool isCapture, float dither)
{
    SkyInputs inputs;
    inputs.direction = direction;
    inputs.cameraPosition = View.cameraPosition;
    inputs.time = View.time;
    inputs.sunDirection = View.skyLights.sunDirection;
    inputs.sunIlluminance = View.skyLights.sunIlluminance;
    inputs.sunAngularRadius = View.skyLights.sunAngularRadius;
    inputs.moonDirection = View.skyLights.moonDirection;
    inputs.moonIlluminance = View.skyLights.moonIlluminance;
    inputs.moonAngularRadius = View.skyLights.moonAngularRadius;
    inputs.wind = View.weather.wind;
    inputs.coverage = View.weather.coverage;
    inputs.density = View.weather.density;
    inputs.cloudColor = View.weather.cloudColor;
    inputs.cloudShadow = View.weather.cloudShadow;
    inputs.isCapture = isCapture;
    inputs.dither = dither;
    return inputs;
}

// The fallback sky's colours, and its luminance per lux of the sun: a clear sky's zenith is around 0.03 to 0.08 of the
// sun's illuminance, in nits.
static const float3 FallbackZenith = float3(0.25, 0.45, 0.95);
static const float3 FallbackHorizon = float3(0.75, 0.85, 1.0);
static const float3 FallbackGround = float3(0.11, 0.105, 0.1);
static const float3 FallbackGlow = float3(1.0, 0.85, 0.6);
static const float FallbackNitsPerLux = 0.06;

// Nits, when there is no sun to scale it.
static const float FallbackNightSky = 0.002;

SkyLight FallbackSky(float3 direction)
{
    float3 towardSun = -View.skyLights.sunDirection;
    float sunLux = Luminance(View.skyLights.sunIlluminance);
    float day = saturate(towardSun.y * 4.0 + 0.2);
    float up = direction.y;
    float3 sky = up >= 0.0
        ? lerp(FallbackHorizon, FallbackZenith, sqrt(saturate(up)))
        : lerp(FallbackHorizon * 0.6, FallbackGround, saturate(-up * 4.0));
    sky += FallbackGlow * (0.6 * pow(saturate(dot(direction, towardSun)), 8.0));
    float radius = max(View.skyLights.sunAngularRadius, 1e-4);
    float disc = dot(direction, towardSun) >= cos(radius) && towardSun.y > 0.0 ? 1.0 : 0.0;

    SkyLight light;
    light.nits = sky * max(sunLux * FallbackNitsPerLux * day, FallbackNightSky);
    light.discs = View.skyLights.sunIlluminance * (disc / (Pi * Pow2(radius)));
    light.emissive = float3(0.0, 0.0, 0.0);
    return light;
}

#endif
