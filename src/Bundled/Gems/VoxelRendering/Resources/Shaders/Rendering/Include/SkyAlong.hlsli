// The world's sky along a direction, for a pass composed after it (Skies.hlsli): its sky shader's, or the renderer's
// fallback without one.

#ifndef MAGIC_RENDERING_SKY_ALONG_HLSLI
#define MAGIC_RENDERING_SKY_ALONG_HLSLI

#include "Rendering/Include/Skies.hlsli"

SkyLight SkyAlong(float3 direction, bool isCapture, float dither)
{
#if SKY_FALLBACK
    return FallbackSky(direction);
#else
    return EvaluateSky(SkyInputsOf(direction, isCapture, dither), LoadMaterialParams(View.skyMaterial));
#endif
}

#endif
