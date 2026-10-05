// What a surface shader (a .surf.hlsl) is written against, and what the renderer's templates wrap it in.
//
// A surface shader declares exactly two things:
//
//   struct MaterialParams { ... };                              // its parameters; a .mat file gives their values
//   Surface EvaluateSurface(SurfaceInputs input, MaterialParams material);
//
// Members of MaterialParams may be float/int/uint/bool, their 2..4 vectors, Color, or TextureRef. A member may
// carry a default ("= float4(1, 1, 1, 1)") and a role for global illumination (": GiColor", ": GiColorMap",
// ": GiEmissive"); the renderer reads both and strips them before the compiler sees the struct. The
// renderer packs a material's values into a GpuMaterial and generates LoadMaterialParams(slot) for the
// shader, so the layout is never written by hand.
//
// Textures live in eight pooled Texture2DArrays, always all bound at READ(0)..READ(7) of the fragment stage,
// so the reflected sampler count is the same for every material pass. A TextureRef is (pool << 16) | layer,
// or TextureNone for "no texture" (the surface's factor stands alone). Whether a texture is sRGB or linear
// is decided when it is put in a pool, so one SampleTexture serves every map; the pool is chosen with a
// constant-index switch because shadercross flattens resource arrays and a runtime index would not survive
// the trip.
//
// A surface never deals with failure: a material whose file, shader or texture is broken is drawn with
// Surfaces/Failure.surf.hlsl instead, chosen by the renderer.

#ifndef MAGIC_SURFACE_HLSLI
#define MAGIC_SURFACE_HLSLI

#include "Include/Bindings.hlsli"
#include "Include/Math.hlsli"
#include "Include/Structs.hlsli"

// The variants a surface is compiled as, defined by the renderer in front of this file.
#ifndef SURFACE_MASKED
#define SURFACE_MASKED 0
#endif
#ifndef SURFACE_DOUBLE_SIDED
#define SURFACE_DOUBLE_SIDED 0
#endif
#ifndef SURFACE_FORWARD
#define SURFACE_FORWARD 0
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
// template (SURFACE_FORWARD) after the eight textures it reads the lighting from too.
#if SURFACE_FORWARD
StructuredBuffer<GpuMaterial> Materials : READ(16);
#else
StructuredBuffer<GpuMaterial> Materials : READ(8);
#endif

typedef uint TextureRef;
static const TextureRef TextureNone = 0xFFFFFFFFu;

// What the template hands a surface: interpolated, normalised, in world space. isFrontFace already accounts
// for mirrored instances (true = the side the triangle's winding says is outside).
struct SurfaceInputs
{
    float3 worldPosition;
    float3 normal;
    float4 tangent; // w = bitangent sign
    float2 uv;
    float2 uvDdx;   // screen-space derivatives of uv, taken once outside any branch
    float2 uvDdy;
    float2 screenUv;
    float time;     // seconds, for animated surfaces
    bool isFrontFace;
};

// What a surface hands back: the inputs of the metallic-roughness model, in linear colour.
struct Surface
{
    float3 baseColor;
    float alpha;
    float3 normal;
    float roughness;
    float metallic;
    float occlusion;
    float3 emissive;
    float alphaCutoff;
};

Surface DefaultSurface(SurfaceInputs input)
{
    Surface surface;
    surface.baseColor = float3(0.8, 0.8, 0.8);
    surface.alpha = 1.0;
    surface.normal = input.normal;
    surface.roughness = 1.0;
    surface.metallic = 0.0;
    surface.occlusion = 1.0;
    surface.emissive = float3(0.0, 0.0, 0.0);
    surface.alphaCutoff = 0.5;
    return surface;
}

bool HasTexture(TextureRef texture)
{
    return texture != TextureNone;
}

// A texture at any uv with that uv's own screen-space derivatives. Explicit gradients because the pool is
// picked in a branch, where the implicit derivatives of Sample are undefined.
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

// A texture at the mesh's own uv: what a map of a surface almost always wants.
float4 SampleTexture(SurfaceInputs input, TextureRef texture)
{
    return SampleTextureGrad(texture, input.uv, input.uvDdx, input.uvDdy);
}

// A tangent-space normal (already mapped to -1..1, any length) into world space through the vertex tangent
// frame. input.normal is unit length already; a mesh without tangents keeps its own normal.
float3 PerturbNormal(SurfaceInputs input, float3 tangentNormal)
{
    float3 normal = input.normal;
    float3 tangent = NormalizeOrZero(input.tangent.xyz - normal * dot(normal, input.tangent.xyz));
    float3 bitangent = cross(normal, tangent) * input.tangent.w;
    return normalize(tangent * tangentNormal.x + bitangent * tangentNormal.y + normal * tangentNormal.z);
}

#endif
