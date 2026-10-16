// What each pixel reflects, as the reflections effect traced and filtered it, before the lighting takes it in, over a
// faint scene; black where a surface is too rough to show any. Teal where the world draws no reflections.

#include "Debug/View.hlsli"

float4 main(PassVaryings input) : SV_Target0
{
    uint2 pixel = ViewPixelOf(input.uv);
#if defined(HAS_Reflections)
    return float4(Lifted(Tonemapped(Reflections.Load(int3(pixel, 0)).rgb), pixel), 1.0);
#else
    return float4(NothingColor, 1.0);
#endif
}
