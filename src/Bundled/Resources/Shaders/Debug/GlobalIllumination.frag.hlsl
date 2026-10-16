// The light from everywhere each pixel takes in, through the lighting's own lookup: global illumination's probes'
// where they hold the place, the sky's past them, as a white matte surface would show it, three stops up as the light
// from everywhere is far below the sun's. Teal where the world draws no global illumination.

#include "Debug/View.hlsli"
#include "Include/Packing.hlsli"
#if defined(HAS_GiIrradiance) && defined(HAS_GiDistance) && defined(HAS_GiLevels)
#include "Lighting/Gi.hlsli"
#include "Lighting/Ibl.hlsli"
#endif

float4 main(PassVaryings input) : SV_Target0
{
    uint2 pixel = ViewPixelOf(input.uv);
#if defined(HAS_GiIrradiance) && defined(HAS_GiDistance) && defined(HAS_GiLevels)
    float depth = Depth.Load(int3(pixel, 0));
    if (depth <= 0.0)
    {
        return float4(0.0, 0.0, 0.0, 1.0);
    }

    float3 seen = ViewPosition((float2)pixel + 0.5, ViewDepth(depth));
    float3 position = ViewToWorld(seen) + CameraPosition;
    float3 view = normalize(-ViewToWorld(seen));
    float3 normal = OctDecode(Normal.Load(int3(pixel, 0)).xy * 2.0 - 1.0);
    float held;
    float3 gathered = GiIrradianceAt(position, normal, view, held);
    float3 sh[9];
    [unroll] for (uint i = 0u; i < 9u; i++)
    {
        sh[i] = LOAD(SkySh, float4, i).rgb;
    }

    float3 taken = lerp(ShIrradiance(normal, sh), gathered, held);
    return float4(Tonemapped(taken * LOAD(Exposure, float4, 0u).x * 8.0), 1.0);
#else
    return float4(NothingColor, 1.0);
#endif
}
