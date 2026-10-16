// The sun's contact shadows, what the shadow maps are too coarse to hold, stretched four times so the little they
// take off shows: lit, the scene in light grey; a quarter shadowed and more, dark. Teal where the world draws none.

#include "Debug/View.hlsli"

float4 main(PassVaryings input) : SV_Target0
{
    uint2 pixel = ViewPixelOf(input.uv);
#if defined(HAS_ContactShadows)
    float shadowed = 1.0 - ContactShadows.Load(int3(pixel, 0)).x;
    return float4(Shaded(1.0 - saturate(shadowed * 4.0), pixel), 1.0);
#else
    return float4(NothingColor, 1.0);
#endif
}
