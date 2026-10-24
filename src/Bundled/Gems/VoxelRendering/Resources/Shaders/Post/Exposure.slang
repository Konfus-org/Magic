// The view's exposure, adapting over time (Lagarde & de Rousiers 2014, "Moving Frostbite to Physically Based
// Rendering": exposure from EV100, 1 / (1.2 * 2^EV100)), metered from the world's light: the EV100 a mid-grey
// surface at the camera reads, lit by the world's lights and the sky, as an incident meter reads it, so what fills the
// view never changes it, only the light does. It is eased towards at a rate that brightens slower than it darkens, as
// eyes do, but by a view's first metering, which is taken whole. The view's exposure row is the exposure, its EV100,
// and whether it was ever metered. One thread: the Exposure post effect's.

module "Post/Exposure";

import Lighting.SkyLight;
import Renderer.World;

// Core's Exposure's constants: the darkest and brightest light the view adapts to, in EV.
struct MeterConstants
{
    float minEv;
    float maxEv;
}

// The reflectance of the grey a light meter is calibrated to.
static const float MidGrey = 0.18;

// How quickly it adapts to a brighter scene and to a darker one, per second.
static const float Darkening = 3.0;
static const float Brightening = 1.0;

// EV100 = log2(L * S / K) for ISO S = 100 and calibration K = 12.5. The point and spot lights count by the light each
// sends the camera's place, from whichever way, unshadowed.
float GetMeteredEv()
{
    float3 up = float3(0.0, 1.0, 0.0);
    float3 local = float3(0.0, 0.0, 0.0);
    [loop] for (uint slot = 0u; slot < World.localLights.count; slot++)
    {
        LocalLight light = World.localLights[slot];
        light.position -= View.cameraPosition;
        local += light.GetIlluminance(float3(0.0, 0.0, 0.0));
    }

    float3 white = GetShIrradiance(World.skySh, up) + (World.lights.GetIlluminance(up) + local) / Pi;
    return log2(max(GetLuminance(white) * MidGrey, 1e-12) * 8.0);
}

[shader("compute")]
[numthreads(1, 1, 1)]
void MeterExposure(uniform PassHeader pass, uniform MeterConstants c)
{
    BeginPass(pass);
    float targetEv = clamp(GetMeteredEv(), c.minEv, c.maxEv);
    float4 held = View.exposure[0u];
    float currentEv = held.z > 0.0 ? log2(1.0 / (1.2 * held.x)) : targetEv;
    float rate = targetEv > currentEv ? Darkening : Brightening;
    float ev = currentEv + (targetEv - currentEv) * (1.0 - exp(-World.clocks.realDeltaSec * rate));
    View.exposure[0u] = float4(1.0 / (1.2 * exp2(ev)), ev, 1.0, 0.0);
}
