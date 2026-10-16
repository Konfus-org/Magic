// The G-buffer's roughness as grey shaded by the scene: black a mirror, white matte.

#include "Debug/View.hlsli"

float4 main(PassVaryings input) : SV_Target0
{
    uint2 pixel = ViewPixelOf(input.uv);
    return float4(Shaded(Normal.Load(int3(pixel, 0)).z, pixel), 1.0);
}
