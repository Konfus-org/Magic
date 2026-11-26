// Copies tiles of the cache of still depth into the local atlas, or clears tiles of the cache, a draw a tile: a
// triangle over the whole target that the draw's scissor keeps to the tile, whose depth is written over what the target
// holds. The cache and the atlas put a tile in the same rectangle, so a pixel reads the cache where it writes. The pass
// header's x is the cache, 0 to clear to no depth.
module "CopyShadowTiles";

import Core.Heap;
import Core.Pass;

struct Varyings
{
    float4 position : SV_Position;
};

[shader("vertex")]
Varyings CopyShadowTileVertex(uint vertex : SV_VertexID)
{
    float2 corner = float2(float((vertex << 1u) & 2u), float(vertex & 2u));
    Varyings output;
    output.position = float4(corner * 2.0 - 1.0, 0.0, 1.0);
    return output;
}

[shader("pixel")]
float CopyShadowTilePixel(Varyings input) : SV_Depth
{
    uint cache = GetPassHeader().x;
    if (cache == 0u)
        return 0.0;

    TextureRef depths = { cache };
    return depths.Load(uint2(input.position.xy), 0u).x;
}
