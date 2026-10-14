// How anything broken looks: magenta, glowing, breathing on a slow sine so it is never mistaken for
// something meant. Whatever shows a failure draws it this way: Surfaces/Failure.surf.hlsl a broken material,
// model or texture, and a renderer what only the GPU knows went wrong.

#ifndef MAGIC_FAILURE_HLSLI
#define MAGIC_FAILURE_HLSLI

#include "Include/Math.hlsli"

static const float3 FailureColor = float3(1.0, 0.0, 1.0);

// Emissive above 1 so the glow still reads as one after the tonemap.
static const float FailureGlow = 2.0;

// The tonemap flattens a saturated colour above ~0.5, so the pulse's floor sits well below it for the
// breathing to be plain, but never at zero: a failed thing stays visible.
static const float PulseMin = 0.05;
static const float PulseMax = 1.0;
static const float PulsePeriod = 2.0; // seconds

float FailurePulse(float time)
{
    float wave = sin(time * 2.0 * Pi / PulsePeriod) * 0.5 + 0.5;
    return lerp(PulseMin, PulseMax, wave);
}

#endif
