// The vertex stage of the G-buffer's resolve: a quad over an 8 x 8 tile of the view, from the instance-rate buffer of
// the tiles a classifying pass gave a resolve bin (its place, 12 bits each way, and the bin in the top 8), cut at the
// view's edge. The bin goes on to the fragment stage, which shades only the tile's pixels of its surface.

#include "Include/Frame.hlsli"
#include "Include/TileVaryings.hlsli"

static const float2 QuadCorners[6] = { float2(0.0, 0.0), float2(1.0, 0.0), float2(1.0, 1.0), float2(0.0, 0.0), float2(1.0, 1.0), float2(0.0, 1.0) };

TileVaryings main(uint tile : TEXCOORD0, uint vertex : SV_VertexID)
{
    float2 place = float2(tile & 0xFFFu, (tile >> 12) & 0xFFFu);
    float2 pixel = min((place + QuadCorners[vertex % 6u]) * 8.0, ViewSize);
    TileVaryings output;
    output.bin = tile >> 24;
    output.clip = float4(pixel.x * ViewTexel.x * 2.0 - 1.0, 1.0 - pixel.y * ViewTexel.y * 2.0, 0.0, 1.0);
    return output;
}
