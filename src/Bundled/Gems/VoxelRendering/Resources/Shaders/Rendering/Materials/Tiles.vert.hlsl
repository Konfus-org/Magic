// The vertex stage of the G-buffer's resolve: a quad over an 8 x 8 tile of the view, each instance of the draw a tile
// of the classifying pass's list (its place, 12 bits each way, and its resolve bin in the top 8), cut at the view's
// edge. The bin goes on to the fragment stage, which shades only the tile's pixels of its surface.

#include "Include/TileVaryings.hlsli"
#include "Rendering/Include/Materials.hlsli"

static const float2 QuadCorners[6] = { float2(0.0, 0.0), float2(1.0, 0.0), float2(1.0, 1.0), float2(0.0, 0.0), float2(1.0, 1.0), float2(0.0, 1.0) };

TileVaryings main(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
{
    LoadView(Draw.view);

    // The draw's first instance is its bin's first tile, which the instance's number counts from.
    uint tile = BUFFER(Draw.tiles).Load(instance * 4u);
    float2 place = float2(tile & 0xFFFu, (tile >> 12) & 0xFFFu);
    float2 pixel = min((place + QuadCorners[vertex % 6u]) * 8.0, View.viewSize);
    TileVaryings output;
    output.bin = tile >> 24;
    output.clip = float4(pixel.x * View.viewTexel.x * 2.0 - 1.0, 1.0 - pixel.y * View.viewTexel.y * 2.0, 0.0, 1.0);
    return output;
}
