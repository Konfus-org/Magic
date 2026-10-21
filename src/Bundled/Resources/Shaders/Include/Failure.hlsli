// How anything broken looks: magenta, glowing, breathing on a slow sine so it is never mistaken for something meant.
// Magenta is a shader's failure: one that does not compile, a pass that does not run, what only the GPU finds wrong.
// Red is an asset that does not load (Surfaces/Failure.surf.hlsl, which the failure materials set the kind of).
// Whatever shows a failure draws it this way. What only the GPU knows went wrong, a shader raises here: a code in the
// frame's Failures buffer (RaiseFailure), which the renderer says in words across the view
// (Include/FailureBanner.hlsli), and the pixel it found it at drawn as the failure (FailurePixel).

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

// What only the GPU finds wrong, a word each of the frame's Failures buffer, 1 once raised, emptied every frame. The
// renderer's FailureHandler.cs holds the words for each, in this order: change both together.
static const uint FailureNaN = 0u;            // a NaN or infinity in the lit scene
static const uint FailureMaterialRange = 1u;  // a material row past the materials there are
static const uint FailureCodeCount = 8u;

float FailurePulse(float time)
{
    float wave = sin(time * 2.0 * Pi / PulsePeriod) * 0.5 + 0.5;
    return lerp(PulseMin, PulseMax, wave);
}

// A pixel shown as the failure: the colour, glowing and breathing.
float3 FailurePixel(float time)
{
    return FailureColor * FailureGlow * FailurePulse(time);
}

// Raises code in the frame's Failures buffer, which a compute pass has that lists Failures among its writes.
void RaiseFailure(RWByteAddressBuffer failures, uint code)
{
    failures.Store(code * 4u, 1u);
}

#endif
