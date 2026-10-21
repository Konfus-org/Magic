// The air and the fog between the camera and what each pixel shows, over the lit scene and the sky, keeping what is
// behind by the share they let through, before anything transparent is drawn.
//
// - The air is the sky's (Include/SkyAir.hlsli): a drawn pixel takes the air in front of it, so the far world fades
//   into the light the sky shows past it; the sky is seen through its own air already, and takes none.
// - The fog is the weather's, as thick at each height as its falloff curve says there, times its amount: how much of it
//   a pixel's ray crosses is the curve's integral over the heights the ray goes through, over how steeply it goes,
//   exact for a curve of straight lines, so nothing is stepped and nothing is noisy; a level ray crosses the fog at the
//   camera's height all the way. It scatters the light that reaches it, tinted by the weather's colour: the sky's from
//   everywhere (its average, from SkySh) and the key light's through the sky's shadow at the camera, forward more than
//   back (Henyey & Greenstein 1941), so it follows day and night, the clouds and the air's colour. The sky is taken to
//   be SkyDistance away. The fog lies low and near, so it is taken to be in front of the air.

#include "Include/Fullscreen.hlsli"
#include "Include/Math.hlsli"
#include "Include/SkyAir.hlsli"
#include "Include/SkyShadow.hlsli"

// Metres the sky is taken to be from the camera.
static const float SkyDistance = 20000.0;

// How much more the fog scatters forward than back, -1 to 1.
static const float ForwardScatter = 0.6;

// The weather's fog in front of a point reach metres off along a direction: its light in nits, not pre-exposed, and in
// w what it lets through.
float4 FogAlong(float3 direction, float reach)
{
    float start = Weather.FogStart;
    [branch] if (reach <= start || Weather.Fog <= 0.0)
    {
        return float4(0.0, 0.0, 0.0, 1.0);
    }

    float eye = CameraPosition.y;
    float rise = direction.y;
    float crossed = abs(rise) > 1e-3
        ? CurveIntegral(Weather.FogFalloff, eye + start * rise, eye + reach * rise) / rise
        : CurveAt(Weather.FogFalloff, eye) * (reach - start);
    float through = exp(-Weather.Fog * max(crossed, 0.0));

    float towardLight = dot(direction, -KeyLightDirection);
    float forward = Pow2(ForwardScatter);
    float phase = (1.0 - forward) / (4.0 * Pi * pow(max(1.0 + forward - 2.0 * ForwardScatter * towardLight, 1e-4), 1.5));
    float3 sky = LOAD(SkySh, float4, 0u).rgb * 0.282095;
    float3 light = KeyLightIlluminance * SkyShadowAt(float3(0.0, 0.0, 0.0)) * phase;
    return float4(Weather.FogColor * (sky + light) * (1.0 - through), through);
}

float4 main(PassVaryings input) : SV_Target0
{
    float depth = Depth.SampleLevel(DepthSampler, input.uv, 0.0);
    bool isDrawn = depth > 0.0;
    float3 seen = ViewPosition(input.position.xy, isDrawn ? ViewDepth(depth) : 1.0);
    float3 ray = ViewToWorld(seen);
    float reach = isDrawn ? length(ray) : SkyDistance;
    float4 air = isDrawn ? SkyAirAt(input.uv, reach) : float4(0.0, 0.0, 0.0, 1.0);
    float4 fog = FogAlong(normalize(ray), reach);
    float3 light = air.rgb * fog.a + fog.rgb;
    return float4(light * LOAD(Exposure, float4, 0u).x, 1.0 - air.a * fog.a);
}
