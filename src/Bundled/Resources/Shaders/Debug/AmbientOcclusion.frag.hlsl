// How enclosed each pixel is, as the ambient occlusion effect worked it out, stretched four times so the little it
// takes off shows: open, the scene in light grey; a quarter enclosed and more, dark. Teal where the world draws no ambient
// occlusion.

#include "Debug/View.hlsli"

float4 main(PassVaryings input) : SV_Target0
{
    uint2 pixel = ViewPixelOf(input.uv);
#if defined(HAS_AmbientOcclusion)
    float occluded = 1.0 - AmbientOcclusion.Load(int3(pixel, 0)).a;
    return float4(Shaded(1.0 - saturate(occluded * 4.0), pixel), 1.0);
#else
    return float4(NothingColor, 1.0);
#endif
}
