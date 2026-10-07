// What a surface shader (a .surf.hlsl) is written against, whatever renderer draws it. A renderer composes the
// surface into its own shaders: it binds the textures and the material's values, generates the function that loads
// them, and defines what this file only declares (SampleTextureGrad; ScenePixelOf and SceneBehind for a forward
// surface).
//
// A surface shader declares exactly two things:
//
//   struct MaterialParams { ... };                              // its parameters; a .mat file gives their values
//   Surface EvaluateSurface(SurfaceInputs input, MaterialParams material);
//
// Members of MaterialParams may be float/int/uint/bool, their 2..4 vectors, Color, or TextureRef. A member may
// carry a default ("= float4(1, 1, 1, 1)") and a role for global illumination (": GiColor", ": GiColorMap",
// ": GiEmissive"); the renderer reads both and strips them before the compiler sees the struct. It packs a
// material's values and generates their loader, so the layout is never written by hand.
//
// A TextureRef names a texture the renderer resolved, or is TextureNone for "no texture" (the surface's factor
// stands alone). Whether a texture is sRGB or linear is decided when it is loaded, so one SampleTexture serves
// every map.
//
// A surface never deals with failure: a material whose file, shader or texture is broken is drawn with
// Surfaces/Failure.surf.hlsl instead, chosen by the renderer.

#ifndef MAGIC_SURFACE_HLSLI
#define MAGIC_SURFACE_HLSLI

#include "Include/Bindings.hlsli"
#include "Include/Math.hlsli"

// 1 when the surface is drawn over the scene already there (a transparent material), which it can then read through
// ScenePixelOf and SceneBehind. The renderer defines it in front of this file.
#ifndef SURFACE_FORWARD
#define SURFACE_FORWARD 0
#endif

typedef uint TextureRef;
static const TextureRef TextureNone = 0xFFFFFFFFu;

// What the renderer hands a surface: interpolated, normalised, in world space. isFrontFace already accounts
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
    float3 cameraPosition; // absolute, like worldPosition
    float3 ambient; // linear: the light that comes from everywhere (the sky's colour)
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

// A texture at any uv with that uv's own screen-space derivatives. Explicit gradients, so it can be called in a
// branch, where the implicit derivatives of Sample are undefined. The renderer defines it over how it keeps textures.
float4 SampleTextureGrad(TextureRef texture, float2 uv, float2 uvDdx, float2 uvDdy);

// A texture at the mesh's own uv: what a map of a surface almost always wants.
float4 SampleTexture(SurfaceInputs input, TextureRef texture)
{
    return SampleTextureGrad(texture, input.uv, input.uvDdx, input.uvDdy);
}

#if SURFACE_FORWARD
// The pixel of the render target a world position lands on. The renderer defines it.
float2 ScenePixelOf(float3 worldPosition);

// What was drawn behind the transparent surfaces (the scene before any of them) at a pixel of the render target,
// kept inside the view. The renderer defines it; a transparent surface that calls it is refractive.
float3 SceneBehind(float2 pixel);
#endif

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
