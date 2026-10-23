// The view's exposure, adapting over time (Lagarde & de Rousiers 2014, "Moving Frostbite to Physically Based
// Rendering": exposure from EV100, 1 / (1.2 * 2^EV100)), metered from the world's light: the EV100 a mid-grey level
// surface in the open reads, lit by the key light, the other directional lights and the sky, as an incident meter reads
// it, so what fills the view never changes it, only the light does. It is eased towards at a rate that brightens slower
// than it darkens, as eyes do, but by a view's first metering, which is taken whole. The row is the exposure, its EV100,
// and whether it was ever metered. One thread: the Exposure post effect's.

#include "Include/Color.hlsli"
#include "Lighting/Ibl.hlsli"
#include "Rendering/Include/View.hlsli"

struct ExposureConstants
{
    uint view;
    uint exposure;           // the view's, read and written
    float minEv;
    float maxEv;
};

CONSTANTS(ExposureConstants, c);

// The reflectance of the grey a light meter is calibrated to.
static const float MidGrey = 0.18;

// How quickly it adapts to a brighter scene and to a darker one, per second.
static const float Darkening = 3.0;
static const float Brightening = 1.0;

// EV100 = log2(L * S / K) for ISO S = 100 and calibration K = 12.5.
float MeteredEv()
{
    float3 sh[9];
    [unroll] for (uint i = 0u; i < 9u; i++)
    {
        sh[i] = BUFFER(View.skySh).Load<float4>(i * 16u).rgb;
    }

    float3 white = ShIrradiance(float3(0.0, 1.0, 0.0), sh);
    white += View.keyLightIlluminance * (saturate(-View.keyLightDirection.y) / Pi);
    [loop] for (uint other = 0u; other < min(View.directionalCount, DirectionalLightCapacity); other++)
    {
        white += View.directional[other].illuminance * (saturate(-View.directional[other].direction.y) / Pi);
    }

    return log2(max(Luminance(white) * MidGrey, 1e-12) * 8.0);
}

[numthreads(1, 1, 1)]
void main()
{
    LoadView(c.view);
    float targetEv = clamp(MeteredEv(), c.minEv, c.maxEv);
    float4 held = BUFFER(c.exposure).Load<float4>(0u);
    float currentEv = held.z > 0.0 ? log2(1.0 / (1.2 * held.x)) : targetEv;
    float rate = targetEv > currentEv ? Darkening : Brightening;
    float ev = currentEv + (targetEv - currentEv) * (1.0 - exp(-View.deltaSeconds * rate));
    BUFFER(c.exposure).Store<float4>(0u, float4(1.0 / (1.2 * exp2(ev)), ev, 1.0, 0.0));
}
