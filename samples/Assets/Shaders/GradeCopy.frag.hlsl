// The tonemapped view copied aside for the grade (Shaders/Grade.frag.hlsl) to read, as a pass cannot read what it
// writes and the grade writes the view back where the output takes it from.

#include "Include/Fullscreen.hlsli"

float4 main(PassVaryings input) : SV_Target0
{
    return float4(Ldr.SampleLevel(LdrSampler, input.uv, 0.0).rgb, 1.0);
}
