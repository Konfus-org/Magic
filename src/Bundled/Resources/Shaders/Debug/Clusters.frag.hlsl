// The clusters as the view drew them: each pixel coloured by the cluster its triangle is of, darker the coarser its
// level, a card grey, and a black line where the pixel beside it or above it is of another; black where nothing was
// drawn, so a crack between clusters shows as a black gap wider than the lines, where it is not against the sky.

#include "Debug/View.hlsli"
#include "Include/Pieces.hlsli"

// The cluster or card a pixel's triangle is of, as the pieces name it, or 0xFFFFFFFF for nothing drawn.
uint PieceAt(uint2 pixel)
{
    uint id = Visibility.Load(int3(pixel, 0));
    return id == 0u ? 0xFFFFFFFFu : VisiblePieces.Load3(((id - 1u) >> 5) * 12u).y;
}

float4 main(PassVaryings input) : SV_Target0
{
    uint2 pixel = ViewPixelOf(input.uv);
    uint what = PieceAt(pixel);
    if (what == 0xFFFFFFFFu)
    {
        return float4(0.0, 0.0, 0.0, 1.0);
    }

    bool isEdge = (pixel.x > 0u && PieceAt(pixel - uint2(1u, 0u)) != what) || (pixel.y > 0u && PieceAt(pixel - uint2(0u, 1u)) != what);
    if (isEdge)
    {
        return float4(0.0, 0.0, 0.0, 1.0);
    }

    if ((what & CardBit) != 0u)
    {
        return float4(0.5, 0.5, 0.5, 1.0);
    }

    uint row = what >> 2;
    GpuCluster cluster = LOAD(Clusters, GpuCluster, row);
    float3 color = HueColor(frac(row * 0.6180339887)) * max(1.0 - 0.12 * ClusterLevel(cluster), 0.3);
    return float4(color, 1.0);
}
