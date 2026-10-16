// The sun's shadow at each pixel, through the same lookup the lighting makes: lit white, shadowed dark blue, over the
// scene. Teal where the world draws no shadows.

#include "Debug/View.hlsli"
#include "Include/Packing.hlsli"
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
    float3 normal = OctDecode(Normal.Load(int3(pixel, 0)).xy * 2.0 - 1.0);
    float lit = SunVirtualShadow(position, normal, viewDepth, 0.5);
    return float4(lerp(float3(0.1, 0.1, 0.5), float3(1.0, 1.0, 1.0), lit) * (0.35 + base), 1.0);
#else
    return float4(NothingColor, 1.0);
#endif
}
