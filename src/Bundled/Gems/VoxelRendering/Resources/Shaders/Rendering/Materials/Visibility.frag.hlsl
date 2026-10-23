// The visibility buffer's bins that do not clip: each pixel takes the name of the triangle in front, which the depth
// test picks, and nothing of its surface is read. Composed with no surface.

#include "Rendering/Include/PieceVaryings.hlsli"

uint main(PiecePlace input) : SV_Target0
{
    return input.id;
}
