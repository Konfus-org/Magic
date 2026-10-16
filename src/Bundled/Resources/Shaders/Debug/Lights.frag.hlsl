// How many point and spot lights the lighting walks at each pixel, from its tile's mask and its z-bin's range, as a
// heat map over the scene: the scene alone for none, blue one, through to white at sixteen and more.

#include "Debug/View.hlsli"
#include "Include/LightCulling.hlsli"

float4 main(PassVaryings input) : SV_Target0
{
    uint2 pixel = ViewPixelOf(input.uv);
    float3 base = OverlayBase(pixel);
#if defined(HAS_LightBins) && defined(HAS_LightTiles)
    float depth = Depth.Load(int3(pixel, 0));
    if (depth <= 0.0)
    {
        return float4(base, 1.0);
    }

    uint2 range = LightBins.Load2(ZBinOf(ViewDepth(depth)) * 8u);
    uint2 tile = pixel / LightTileSize;
    uint words = (tile.y * TileCountX + tile.x) * TileWords;
    uint count = 0u;
    if (range.x <= range.y)
    {
        [loop] for (uint word = range.x >> 5u; word <= range.y >> 5u; word++)
        {
            count += countbits(LightTiles.Load((words + word) * 4u) & RangeMask(word, range.x, range.y));
        }
    }

    return float4(count == 0u ? base : lerp(base, HeatColor((float)(count - 1u) / 15.0), 0.75), 1.0);
#else
    return float4(NothingColor, 1.0);
#endif
}
