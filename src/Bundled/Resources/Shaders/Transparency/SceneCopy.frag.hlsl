// The lit scene and its sky, copied before any transparent surface is drawn over it: what a refracting surface shows
// behind it (Include/SurfaceBindings.hlsli's SceneBehind), its mips made after as its blurrier copies.

#include "Include/Fullscreen.hlsli"

float4 main(PassVaryings input) : SV_Target0
{
    return float4(Hdr.SampleLevel(HdrSampler, input.uv, 0.0).rgb, 1.0);
}
