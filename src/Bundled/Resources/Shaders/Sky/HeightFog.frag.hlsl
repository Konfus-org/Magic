// Height fog: the air between the camera and each pixel scattering light toward it and hiding what is behind, as thick
// at each height as the falloff curve says there, times the intensity: the curve's at is metres above the world's
// origin, its value the share of the intensity there. How much fog a pixel's ray crosses is the curve's integral over
// the heights the ray goes through, over how steeply it goes, exact for a curve of straight lines, so nothing is
// stepped and nothing is noisy; a level ray crosses the fog at the camera's height all the way. The fog scatters its
// colour of the light that reaches it: the sky's from everywhere (its average, from SkySh) and the sun's, scattered
// forward more than back (Henyey & Greenstein 1941), so it follows day and night and the far world takes the sky's
// colour. It is composited over the lit scene and the sky, keeping what is behind by the share it lets through, before
// anything transparent is drawn. The sky is taken to be SkyDistance away.

#include "Include/Curve.hlsli"
#include "Include/Fullscreen.hlsli"
#include "Include/Math.hlsli"

struct PassParams
{
    Color color = Color(0.6, 0.7, 0.85, 1.0);  // the share of the light reaching it the fog scatters, by colour
    float intensity = 0.002;                    // how much fog there is, a metre, where the falloff is 1
    float start = 0.0;                          // metres from the camera before the fog begins
    Curve falloff;                              // the share of intensity by metres above the world's origin
};

// Metres the sky is taken to be from the camera.
static const float SkyDistance = 20000.0;

// How much more the fog scatters forward than back, -1 to 1.
static const float ForwardScatter = 0.6;

float4 main(PassVaryings input) : SV_Target0
{
    PassParams params = LoadPassParams();
    float depth = Depth.SampleLevel(DepthSampler, input.uv, 0.0);
    float3 seen = ViewPosition(input.position.xy, depth > 0.0 ? ViewDepth(depth) : 1.0);
    float3 ray = ViewToWorld(seen);
    float distance = depth > 0.0 ? length(ray) : SkyDistance;
    float3 direction = normalize(ray);
    [branch] if (distance <= params.start || params.intensity <= 0.0)
    {
        return float4(0.0, 0.0, 0.0, 0.0);
    }

    float eye = CameraPosition.y;
    float rise = direction.y;
    float crossed = abs(rise) > 1e-3
        ? CurveIntegral(params.falloff, eye + params.start * rise, eye + distance * rise) / rise
        : CurveAt(params.falloff, eye) * (distance - params.start);
    float through = exp(-params.intensity * max(crossed, 0.0));

    float towardSun = dot(direction, -SunDirection);
    float phase = (1.0 - Pow2(ForwardScatter)) / (4.0 * Pi * pow(max(1.0 + Pow2(ForwardScatter) - 2.0 * ForwardScatter * towardSun, 1e-4), 1.5));
    float3 sky = LOAD(SkySh, float4, 0u).rgb * 0.282095;
    float3 scattered = params.color.rgb * (sky + SunIlluminance * phase);
    float fogged = 1.0 - through;
    return float4(scattered * LOAD(Exposure, float4, 0u).x * fogged, fogged);
}
