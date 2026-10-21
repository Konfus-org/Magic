// How this renderer binds a surface written against Core's Include/Surface.hlsli: the eight texture pools, which the
// renderer declares ahead of this with the materials buffer and everything else the draw pass can see, and
// SampleTextureGrad over them; for a forward surface, the scene behind (ScenePixelOf, SceneBehind) over SceneColor. A
// surface pipeline is bound only what it uses, which the renderer reads from its compile.
//
// A TextureRef is (pool << 16) | layer. Pools are colour (sRGB) then data (linear), each 256, 512, 1024 and 2048 texels
// a side, as the renderer's GpuCache.cs makes them. The pool is picked by a switch of constant indexes, since a
// runtime index into resources does not survive translation to every API.

#ifndef MAGIC_SURFACE_BINDINGS_HLSLI
#define MAGIC_SURFACE_BINDINGS_HLSLI

#include "Include/Bindings.hlsli"
#include "Include/Surface.hlsli"

float4 SampleTextureGrad(TextureRef texture, float2 uv, float2 uvDdx, float2 uvDdy)
{
    float3 location = float3(uv, (float)(texture & 0xFFFFu));
    float4 color;
    [branch] switch (texture >> 16)
    {
        case 0u:
            color = PoolSrgb256.SampleGrad(PoolSrgb256Sampler, location, uvDdx, uvDdy);
            break;
        case 1u:
            color = PoolSrgb512.SampleGrad(PoolSrgb512Sampler, location, uvDdx, uvDdy);
            break;
        case 2u:
            color = PoolSrgb1024.SampleGrad(PoolSrgb1024Sampler, location, uvDdx, uvDdy);
            break;
        case 3u:
            color = PoolSrgb2048.SampleGrad(PoolSrgb2048Sampler, location, uvDdx, uvDdy);
            break;
        case 4u:
            color = PoolLinear256.SampleGrad(PoolLinear256Sampler, location, uvDdx, uvDdy);
            break;
        case 5u:
            color = PoolLinear512.SampleGrad(PoolLinear512Sampler, location, uvDdx, uvDdy);
            break;
        case 6u:
            color = PoolLinear1024.SampleGrad(PoolLinear1024Sampler, location, uvDdx, uvDdy);
            break;
        default:
            color = PoolLinear2048.SampleGrad(PoolLinear2048Sampler, location, uvDdx, uvDdy);
            break;
    }

    return color;
}

#if SURFACE_FORWARD
float2 ScenePixelOf(float3 worldPosition)
{
    float4 clip = mul(ViewProj, float4(worldPosition - CameraPosition, 1.0));
    float2 ndc = clip.xy / max(clip.w, 1e-5);
    return float2(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5) * ViewSize;
}

// SceneColor's mips are its blurrier copies, the rougher the further down.
float3 SceneBehind(float2 pixel, float roughness)
{
    float width;
    float height;
    float levels;
    SceneColor.GetDimensions(0u, width, height, levels);
    float2 inside = clamp(pixel, 0.5, ViewSize - 0.5);
    return SceneColor.SampleLevel(SceneColorSampler, inside * ViewTexel, saturate(roughness) * (levels - 1.0)).rgb;
}
#endif

#endif
