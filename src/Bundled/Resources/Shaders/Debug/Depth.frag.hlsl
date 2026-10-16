// The view's depth: white a metre away, black four kilometres out, a shade a doubling, with a dark contour at each
// doubling so shapes read at any distance; black where nothing was drawn.

#include "Debug/View.hlsli"

float4 main(PassVaryings input) : SV_Target0
{
    uint2 pixel = ViewPixelOf(input.uv);
    float depth = Depth.Load(int3(pixel, 0));
    if (depth <= 0.0)
    {
        return float4(0.0, 0.0, 0.0, 1.0);
    }

    float doublings = log2(max(ViewDepth(depth), 1.0));
    float grey = 1.0 - saturate(doublings / 12.0);
    float contour = frac(doublings) < 0.04 ? 0.5 : 1.0;
    return float4((grey * contour).xxx, 1.0);
}
