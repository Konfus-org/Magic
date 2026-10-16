// The view's exposure, adapting over time (Lagarde & de Rousiers 2014, "Moving Frostbite to Physically Based
// Rendering": exposure from EV100, 1 / (1.2 * 2^EV100)), metered one of two ways, by what its pass reads:
// - the view's pixels (ExposureHistogram): the mean EV100 of the histogram's pixels between its darkest and brightest
//   shares, so it follows what the view sees, a dark wall filling it brightening everything;
// - the world's light (SkySh): the EV100 a mid-grey level surface in the open reads, lit by the sun and the sky, as an
//   incident meter reads it, so what fills the view never changes it, only the light does: for a world its sun and
//   sky light.
// Either is eased towards at a rate that brightens slower than it darkens, as eyes do. The view's lighting is
// pre-exposed with it from the next frame on.

#include "Include/Color.hlsli"
#if defined(HAS_SkySh)
#include "Lighting/Ibl.hlsli"
#endif

// Bins of the histogram (Post/ExposureHistogram.comp.hlsl).
static const uint Bins = 64u;

// The reflectance of the grey a light meter is calibrated to.
static const float MidGrey = 0.18;

struct PassParams
{
    float minEv = -4.0;
    float maxEv = 16.0;
    float compensation = 0.0;    // EV added to what the scene asks for
    float darkShare = 0.5;       // the share of pixels, darkest first, left out
    float brightShare = 0.95;    // the share of pixels, darkest first, past which they are left out
    float darkening = 3.0;       // how quickly it adapts to a brighter scene, per second
    float brightening = 1.0;     // how quickly it adapts to a darker scene, per second
};

#if defined(HAS_SkySh)
// The EV100 of a mid-grey level surface in the open, lit by the sun and the sky: EV100 = log2(L * S / K) for ISO
// S = 100 and calibration K = 12.5, as the histogram's pixels are binned (Post/ExposureHistogram.comp.hlsl).
float MeteredEv(PassParams params)
{
    float3 sh[9];
    [unroll] for (uint i = 0u; i < 9u; i++)
    {
        sh[i] = LOAD(SkySh, float4, i).rgb;
    }

    float3 white = ShIrradiance(float3(0.0, 1.0, 0.0), sh) + SunIlluminance * (saturate(-SunDirection.y) / Pi);
    return log2(max(Luminance(white) * MidGrey, 1e-12) * 8.0);
}
#else
// The mean EV100 of the histogram's pixels between its darkest and brightest shares.
float MeteredEv(PassParams params)
{
    uint total = 0u;
    for (uint bin = 1u; bin < Bins; bin++)
    {
        total += ExposureHistogram.Load(bin * 4u);
    }

    float from = (float)total * params.darkShare;
    float to = (float)total * params.brightShare;
    float counted = 0.0;
    float weighted = 0.0;
    float seen = 0.0;
    for (uint each = 1u; each < Bins; each++)
    {
        float count = (float)ExposureHistogram.Load(each * 4u);
        float kept = max(min(seen + count, to) - max(seen, from), 0.0);
        float ev = params.minEv + ((float)each - 0.5) / (float)(Bins - 2u) * (params.maxEv - params.minEv);
        weighted += kept * ev;
        counted += kept;
        seen += count;
    }

    return counted > 0.0 ? weighted / counted : 0.0;
}
#endif

[numthreads(1, 1, 1)]
void main()
{
    PassParams params = LoadPassParams();
    float targetEv = clamp(MeteredEv(params), params.minEv, params.maxEv) - params.compensation;
    float current = LOAD(Exposure, float4, 0u).x;
    float currentEv = current > 0.0 ? log2(1.0 / (1.2 * current)) : targetEv;
    float rate = targetEv > currentEv ? params.darkening : params.brightening;
    float ev = currentEv + (targetEv - currentEv) * (1.0 - exp(-DeltaSeconds * rate));
    STORE(Exposure, float4, 0u, float4(1.0 / (1.2 * exp2(ev)), ev, 0.0, 0.0));
}
