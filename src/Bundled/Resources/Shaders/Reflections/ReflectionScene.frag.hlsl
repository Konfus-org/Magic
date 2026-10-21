// The lit scene and its sky in nits, kept for the next frame's reflections (Reflections/ReflectionTrace.comp.hlsl), which
// trace the view before it is lit and so read what it showed the frame before: copied before any transparent surface is
// drawn over it, with exposure taken back out, so a change of exposure from one frame to the next does not show in
// them, and held below what a half float holds, as the sun's disc can be brighter; its alpha one, so an empty history,
// a first frame's, tells itself apart; its mips made after as its blurrier copies, for a rougher surface.

#include "Include/Bindings.hlsli"
#include "Include/Fullscreen.hlsli"

float4 main(PassVaryings input) : SV_Target0
{
    float3 seen = Hdr.SampleLevel(HdrSampler, input.uv, 0.0).rgb / max(LOAD(Exposure, float4, 0u).x, 1e-12);
    return float4(min(seen, 60000.0), 1.0);
}
