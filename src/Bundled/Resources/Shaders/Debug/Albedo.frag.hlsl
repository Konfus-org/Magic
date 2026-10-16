// The G-buffer's base colour, unlit.

#include "Debug/View.hlsli"

float4 main(PassVaryings input) : SV_Target0
{
    uint2 pixel = ViewPixelOf(input.uv);
    return float4(Albedo.Load(int3(pixel, 0)).rgb, 1.0);
}
