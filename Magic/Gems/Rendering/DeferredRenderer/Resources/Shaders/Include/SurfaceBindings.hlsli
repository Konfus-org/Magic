// How this renderer binds a surface shader written against Core's Include/Surface.hlsli: the variants it compiles
// one as, the texture pools and SampleTextureGrad over them, the material records the generated LoadMaterialParams
// reads, and for a forward surface the scene behind (ScenePixelOf, SceneBehind). Pipelines.Compose includes it in
// front of every surface; whatever else samples the pools includes it too (Include/MeshFragment.hlsli, the
// templates, Shadows/ShadowImpostor.frag.hlsl).
//
// Textures live in eight pooled Texture2DArrays, always all bound at READ(0)..READ(7) of the fragment stage, so the
// reflected sampler count is the same for every material pass. A TextureRef is (pool << 16) | layer; the pool is
// chosen with a constant-index switch because shadercross flattens resource arrays and a runtime index would not
// survive the trip.

#ifndef MAGIC_SURFACE_BINDINGS_HLSLI
#define MAGIC_SURFACE_BINDINGS_HLSLI

#include "Include/Bindings.hlsli"
#include "Include/Structs.hlsli"
#include "Include/Surface.hlsli"

// The variants a surface is compiled as, defined by Pipelines.Compose in front of this file (SURFACE_FORWARD's
// default is the contract's).
#ifndef SURFACE_MASKED
#define SURFACE_MASKED 0
#endif
#ifndef SURFACE_DOUBLE_SIDED
#define SURFACE_DOUBLE_SIDED 0
#endif
#ifndef SURFACE_IMPOSTOR
#define SURFACE_IMPOSTOR 0
#endif

// The texture pools (TextureTable.cs): pool = formatIndex * 4 + sizeIndex, formats [sRGB, linear], sizes
// [256, 512, 1024, 2048]. A pool's index is its register index.
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

// Storage buffers follow the sampled textures in SDL's binding order: after the pools, and in the forward
// template (SURFACE_FORWARD) after the textures it reads the lighting from, the scene behind and the nearest
// transparent depth (Templates/Forward.frag.hlsl).
#if SURFACE_FORWARD
#include "Include/Frame.hlsli"

// What was drawn behind the transparent surfaces: the lit opaque scene and the sky, copied before any of them
// (TransparentPrepare.pass), so a surface can show it bent, blurred or tinted (refraction).
Texture2D SceneColor : READ(13);
SamplerState SceneColorSampler : SAMPLER(13);
StructuredBuffer<GpuMaterial> Materials : READ(15);

// The pixel of the render target a world position lands on.
float2 ScenePixelOf(float3 worldPosition)
{
    float4 clip = mul(ViewProj, float4(worldPosition - CameraPos, 1.0));
    float2 ndc = clip.xy / max(clip.w, 1e-5);
    return ViewOrigin + float2(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5) * ViewSize;
}

// The scene behind at a pixel of the render target, kept inside the view.
float3 SceneBehind(float2 pixel)
{
    float width;
    float height;
    SceneColor.GetDimensions(width, height);
    float2 inside = clamp(pixel, ViewOrigin + 0.5, ViewOrigin + ViewSize - 0.5);
    return SceneColor.SampleLevel(SceneColorSampler, inside / float2(width, height), 0.0).rgb;
}
#else
StructuredBuffer<GpuMaterial> Materials : READ(8);
#endif

// The pool switch: explicit gradients because the pool is picked in a branch.
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

#endif
