// How this renderer binds a surface written against Core's Include/Surface.hlsli: the eight texture pools, always bound
// at READ(0) to READ(7) of the fragment stage, and SampleTextureGrad over them; for a forward surface, the scene behind
// (ScenePixelOf, SceneBehind) over SceneColor, which its draw pass reads. The renderer declares the materials buffer and
// what the draw pass reads ahead of this, after the pools; KeepBindingsAlive keeps every one of them, whatever the
// surface uses, so they stay where the renderer binds them.
//
// A TextureRef is (pool << 16) | layer. Pools are colour (sRGB) then data (linear), each 256, 512, 1024 and 2048 texels
// a side, as the renderer's TexturePools.cs makes them. The pool is picked by a switch of constant indexes, since a
// runtime index into resources does not survive translation to every API.

#ifndef MAGIC_SURFACE_BINDINGS_HLSLI
#define MAGIC_SURFACE_BINDINGS_HLSLI

#include "Include/Bindings.hlsli"
#include "Include/Surface.hlsli"

Texture2DArray PoolSrgb256 : READ(0);
SamplerState PoolSrgb256Sampler : SAMPLER(0);
Texture2DArray PoolSrgb512 : READ(1);
SamplerState PoolSrgb512Sampler : SAMPLER(1);
Texture2DArray PoolSrgb1024 : READ(2);
SamplerState PoolSrgb1024Sampler : SAMPLER(2);
Texture2DArray PoolSrgb2048 : READ(3);
SamplerState PoolSrgb2048Sampler : SAMPLER(3);
Texture2DArray PoolLinear256 : READ(4);
SamplerState PoolLinear256Sampler : SAMPLER(4);
Texture2DArray PoolLinear512 : READ(5);
SamplerState PoolLinear512Sampler : SAMPLER(5);
Texture2DArray PoolLinear1024 : READ(6);
SamplerState PoolLinear1024Sampler : SAMPLER(6);
Texture2DArray PoolLinear2048 : READ(7);
SamplerState PoolLinear2048Sampler : SAMPLER(7);

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

// Zero, but for a flags value no instance has: keeps every pool, the materials buffer and every read of the draw pass
// (the header's KeepReadsAlive) bound however little the surface reads, as the compiler drops what a shader does not
// use and every binding after it would move.
float3 KeepBindingsAlive(uint flags, float2 uv)
{
    float3 nothing = float3(0.0, 0.0, 0.0);
    [branch] if (flags == 0xFFFFFFFFu)
    {
        [unroll] for (uint pool = 0u; pool < 8u; pool++)
        {
            nothing += SampleTextureGrad(pool << 16, uv, float2(0.0, 0.0), float2(0.0, 0.0)).rgb;
        }

        nothing += asfloat(LOAD(Materials, GpuMaterial, flags).rows[0].xyz);
        nothing += KeepReadsAlive(uv);
    }

    return nothing;
}

#endif
