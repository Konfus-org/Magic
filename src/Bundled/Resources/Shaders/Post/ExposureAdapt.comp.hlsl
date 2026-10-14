// The view's exposure, adapting over time to how bright it is (Lagarde & de Rousiers 2014, "Moving Frostbite to
// Physically Based Rendering": exposure from EV100, 1 / (1.2 * 2^EV100)): the mean EV100 of the histogram's pixels
// between its darkest and brightest shares, eased towards at a rate that brightens slower than it darkens, as eyes do.
// The view's lighting is pre-exposed with it from the next frame on.

#define BINS 64u

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

[numthreads(1, 1, 1)]
void main()
{
    PassParams params = LoadPassParams();
    uint total = 0u;
    for (uint bin = 1u; bin < BINS; bin++)
    {
        total += ExposureHistogram.Load(bin * 4u);
    }

    float from = (float)total * params.darkShare;
    float to = (float)total * params.brightShare;
    float counted = 0.0;
    float weighted = 0.0;
    float seen = 0.0;
    for (uint each = 1u; each < BINS; each++)
    {
        float count = (float)ExposureHistogram.Load(each * 4u);
        float kept = max(min(seen + count, to) - max(seen, from), 0.0);
        float ev = params.minEv + ((float)each - 0.5) / (float)(BINS - 2u) * (params.maxEv - params.minEv);
        weighted += kept * ev;
        counted += kept;
        seen += count;
    }

    float targetEv = (counted > 0.0 ? weighted / counted : 0.0) - params.compensation;
    float current = LOAD(Exposure, float4, 0u).x;
    float currentEv = current > 0.0 ? log2(1.0 / (1.2 * current)) : targetEv;
    float rate = targetEv > currentEv ? params.darkening : params.brightening;
    float ev = currentEv + (targetEv - currentEv) * (1.0 - exp(-DeltaSeconds * rate));
    STORE(Exposure, float4, 0u, float4(1.0 / (1.2 * exp2(ev)), ev, 0.0, 0.0));
}
