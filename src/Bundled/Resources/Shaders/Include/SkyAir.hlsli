// The air in front of a view, from the world's sky (Include/Sky.hlsli's EvaluateAir): what it scatters toward the
// camera and lets through of what is behind, which a sky draw of its own lays into the view's SkyAir
// (Templates/SkyAir.frag.hlsl), and the air over the view reads back by SkyAirAt.
//
// SkyAir is a coarse volume over the view (Hillaire 2020, "A Scalable and Production Ready Sky and Atmosphere Rendering
// Technique", its aerial perspective): a texel for each way through the view, a layer for each slice of distance from
// the camera, the slices closer together near it, where the world is seen finest. The air changes slowly across the
// view and with distance, so a point of the view takes it between the texels and slices about it, and a place farther
// than the last slice takes the last slice's.

#ifndef MAGIC_SKY_AIR_HLSLI
#define MAGIC_SKY_AIR_HLSLI

#include "Include/Frame.hlsli"
#include "Include/Math.hlsli"

// The slices of distance, SkyAir's layers and the iterations of the pass that draws it, and the metres the last reaches.
static const uint SkyAirSlices = 32u;
static const float SkyAirFarthest = 32000.0;

// Metres from the camera a slice's air reaches to.
float SkyAirReach(uint slice)
{
    return SkyAirFarthest * Pow2((float)(slice + 1u) / (float)SkyAirSlices);
}

// The air in front of a point of the view at uv, reach metres off: its light in nits, not pre-exposed, and in w what it
// lets through; none where the frame draws no air.
float4 SkyAirAt(float2 uv, float reach)
{
#if defined(HAS_SkyAir)
    float slice = sqrt(saturate(reach / SkyAirFarthest)) * (float)SkyAirSlices - 1.0;
    float nearer = floor(slice);
    float4 closer = nearer < 0.0
        ? float4(0.0, 0.0, 0.0, 1.0)
        : SkyAir.SampleLevel(SkyAirSampler, float3(uv, nearer), 0.0);
    float4 farther = SkyAir.SampleLevel(SkyAirSampler, float3(uv, min(nearer + 1.0, (float)(SkyAirSlices - 1u))), 0.0);
    return lerp(closer, farther, slice - nearer);
#else
    return float4(0.0, 0.0, 0.0, 1.0);
#endif
}

#endif
