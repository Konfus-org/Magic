// Each triangle the view drew in a colour of its own, so how finely the clusters are cut shows; black where nothing was
// drawn. Coloured by its instance, its cluster's piece and its place in it, which hold from frame to frame, never by
// the piece's slot among the view's pieces, which the visibility buffer names it by: the slots are handed out anew each
// frame, in whatever order the waves run.

#include "Debug/View.hlsli"

float4 main(PassVaryings input) : SV_Target0
{
    uint2 pixel = ViewPixelOf(input.uv);
    uint id = Visibility.Load(int3(pixel, 0));
    if (id == 0u)
    {
        return float4(0.0, 0.0, 0.0, 1.0);
    }

    uint3 piece = VisiblePieces.Load3(((id - 1u) >> 5) * 12u);
    uint face = (id - 1u) & 31u;
    return float4(IdColor(((piece.y << 5) | face) ^ (piece.x * 2654435761u)), 1.0);
}
