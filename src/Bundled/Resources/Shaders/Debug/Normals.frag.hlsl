// The G-buffer's normals, in world space, as colour: +X red, +Y green, +Z blue; black where nothing was drawn.

#include "Debug/View.hlsli"
#include "Include/Packing.hlsli"

float4 main(PassVaryings input) : SV_Target0
{
    uint2 pixel = ViewPixelOf(input.uv);
    if (Depth.Load(int3(pixel, 0)) <= 0.0)
    {
        return float4(0.0, 0.0, 0.0, 1.0);
    }

    float3 normal = OctDecode(Normal.Load(int3(pixel, 0)).xy * 2.0 - 1.0);
    return float4(normal * 0.5 + 0.5, 1.0);
}
