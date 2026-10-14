// The sky template: one triangle over what nothing was drawn on (depth tested against the view's), or over a face of a
// cube when it captures the sky (SKY_CUBE) for the ambient light. The world's sky is its material's surface, composed
// before this, evaluated as seen along each direction; its emission is the sky's luminance in nits, not pre-exposed,
// so it lights the world as the sky would. Without one (SKY_FALLBACK) it is the renderer's own: a clear sky as bright
// as the sun makes it, with the sun's disc in view but not in the capture, which the sun's own light stands for.

#include "Include/Cube.hlsli"
#include "Include/Fullscreen.hlsli"
#include "Include/Math.hlsli"

// The fallback sky's colours, and its luminance per lux of the sun: a clear sky's zenith is around 0.03 to 0.08 of the
// sun's illuminance, in nits.
static const float3 Zenith = float3(0.25, 0.45, 0.95);
static const float3 Horizon = float3(0.75, 0.85, 1.0);
static const float3 Ground = float3(0.11, 0.105, 0.1);
static const float3 Glow = float3(1.0, 0.85, 0.6);
static const float NitsPerLux = 0.06;

// Nits, when there is no sun to scale it.
static const float NightSky = 0.002;

float3 FallbackSky(float3 direction)
{
    float3 towardSun = -SunDirection;
    float sunLux = dot(SunIlluminance, float3(0.2126, 0.7152, 0.0722));
    float day = saturate(towardSun.y * 4.0 + 0.2);
    float up = direction.y;
    float3 sky = up >= 0.0 ? lerp(Horizon, Zenith, sqrt(saturate(up))) : lerp(Horizon * 0.6, Ground, saturate(-up * 4.0));
    sky += Glow * (0.6 * pow(saturate(dot(direction, towardSun)), 8.0));
    return sky * max(sunLux * NitsPerLux * day, NightSky);
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
    float3 nits = float3(0.0, 0.0, 0.0);
#if SKY_FALLBACK
    nits = FallbackSky(direction);
#else
    // Where on an equirectangular image the direction looks, its derivatives taken across the seam the short way.
    float2 uv = float2(atan2(direction.x, direction.z) * (0.5 * InvPi) + 0.5, FastAcos(clamp(direction.y, -1.0, 1.0)) * InvPi);
    float2 uvDdx = ddx(uv);
    float2 uvDdy = ddy(uv);
    uvDdx.x -= round(uvDdx.x);
    uvDdy.x -= round(uvDdy.x);

    SurfaceInputs inputs;
    inputs.worldPosition = CameraPosition + direction * 10000.0;
    inputs.normal = -direction;
    inputs.tangent = float4(0.0, 0.0, 0.0, 1.0);
    inputs.uv = uv;
    inputs.uvDdx = uvDdx;
    inputs.uvDdy = uvDdy;
    inputs.screenUv = input.uv;
    inputs.time = Time;
    inputs.cameraPosition = CameraPosition;
    inputs.viewDirection = -direction;
    inputs.isFrontFace = true;
    nits = EvaluateSurface(inputs, LoadMaterialParams(SkyMaterial)).emissive;
#endif

    nits += KeepBindingsAlive(SkyMaterial | 0x7FFFFFFFu, input.uv);
#if SKY_CUBE
    return float4(nits, 1.0);
#else
    float sunDisc = dot(direction, -SunDirection) >= cos(SunAngularRadius) ? 1.0 : 0.0;
    float3 sun = SunIlluminance * (sunDisc / (Pi * Pow2(SunAngularRadius)));
    return float4((nits + sun * SKY_FALLBACK) * LOAD(Exposure, float4, 0u).x, 1.0);
#endif
}
