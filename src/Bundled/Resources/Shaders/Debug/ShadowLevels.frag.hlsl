// Which level of the sun's clipmap each pixel reads its shadow from, a colour per level, finest first round the wheel,
// over the scene; dimmed where the level it wants is still waiting to be drawn and a coarser one is read instead, and
// teal where no level holds a drawn page for it.

#include "Debug/View.hlsli"
#if defined(HAS_ShadowTables) && defined(HAS_ShadowPool) && defined(HAS_SunClipmap)
#include "Include/VirtualShadows.hlsli"
#endif

float4 main(PassVaryings input) : SV_Target0
{
    uint2 pixel = ViewPixelOf(input.uv);
    float3 base = OverlayBase(pixel);
#if defined(HAS_ShadowTables) && defined(HAS_ShadowPool) && defined(HAS_SunClipmap)
    float depth = Depth.Load(int3(pixel, 0));
    if (depth <= 0.0)
    {
        return float4(base, 1.0);
    }

    float viewDepth = ViewDepth(depth);
    float3 position = ViewToWorld(ViewPosition((float2)pixel + 0.5, viewDepth));
    GpuSunClipmap sun = SunClipmapOf();
    uint wanted = SunLevelFor(sun, viewDepth, 0.5);
    uint drawn = wanted;
    uint page;
    float2 place;
    float3 color = NothingColor;
    if (SunDrawnAt(sun, drawn, position, page, place))
    {
        color = HueColor((float)(wanted % 8u) / 8.0) * (drawn == wanted ? 1.0 : 0.35);
    }

    return float4(lerp(base, color, 0.7), 1.0);
#else
    return float4(NothingColor, 1.0);
#endif
}
