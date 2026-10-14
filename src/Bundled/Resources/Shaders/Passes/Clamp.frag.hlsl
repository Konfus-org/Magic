// The default pass's shader (Passes/Default.pass): the lit scene shown as it is, with no tonemapping curve, clamped to
// what a display shows. Its Hdr is declared ahead of it by the renderer, from the pass's file.

#include "Include/Fullscreen.hlsli"

float4 main(PassVaryings input) : SV_Target0
{
    return float4(saturate(Hdr.SampleLevel(HdrSampler, input.uv, 0.0).rgb), 1.0);
}
