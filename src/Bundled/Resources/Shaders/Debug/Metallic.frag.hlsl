// The G-buffer's metallic as grey shaded by the scene: black a dielectric, white a metal.

#include "Debug/View.hlsli"

float4 main(PassVaryings input) : SV_Target0
{
    uint2 pixel = ViewPixelOf(input.uv);
    return float4(Shaded(Material.Load(int3(pixel, 0)).r, pixel), 1.0);
}
