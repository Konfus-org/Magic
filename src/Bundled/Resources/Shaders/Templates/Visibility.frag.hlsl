// The visibility buffer's bins that do not clip: each pixel takes the name of the triangle in front, which the depth
// test picks, and nothing of its surface is read. Composed with no surface, after the draw's bindings, which it shares
// with the bins that clip.

#include "Include/PieceVaryings.hlsli"

uint main(PiecePlace input) : SV_Target0
{
    // Every binding the draw shares with the bins that clip is kept by a name no triangle has.
    float kept = KeepBindingsAlive(input.id == 0xFFFFFFFFu ? 0xFFFFFFFFu : 0u, float2(0.0, 0.0)).x;
    return input.id + (kept != 0.0 ? 1u : 0u);
}
