// What the surfaces emit, alone: the scene colour as the G-buffer left it, before the lighting adds to it, so this view
// runs in the G-buffer stage; over a faint scene, so its shape reads where nothing emits.

#include "Debug/View.hlsli"

float4 main(PassVaryings input) : SV_Target0
{
    uint2 pixel = ViewPixelOf(input.uv);
    return float4(Lifted(Tonemapped(Hdr.Load(int3(pixel, 0)).rgb), pixel), 1.0);
}
