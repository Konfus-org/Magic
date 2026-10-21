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
// ": GiMetallic", ": GiEmissive"); the renderer reads both and strips them before the compiler sees the struct. It packs a
// material's values and generates their loader, so the layout is never written by hand.
//
// A TextureRef names a texture the renderer resolved, or is TextureNone for "no texture" (the surface's factor
// stands alone). Whether a texture is sRGB or linear is decided when it is loaded, so one SampleTexture serves
// every map.
//
// A surface never deals with failure: a material whose file, shader or texture is broken is drawn with
// Surfaces/Failure.surf.hlsl instead, chosen by the renderer. It never lights itself either: it says what it is
// made of, and the renderer lights that (Lighting/Lights.hlsli), its reflections included.

#ifndef MAGIC_SURFACE_HLSLI
#define MAGIC_SURFACE_HLSLI

#include "Include/Bindings.hlsli"
#include "Include/Math.hlsli"
#include "Include/ShadingModel.hlsli"

// 1 when the surface is drawn over the scene already there (a transparent material), which it can then read through
// ScenePixelOf and SceneBehind. The renderer defines it in front of this file.
#ifndef SURFACE_FORWARD
#define SURFACE_FORWARD 0
#endif

typedef uint TextureRef;
static const TextureRef TextureNone = 0xFFFFFFFFu;

// What the renderer hands a surface, in world space. normal is unit length; tangent is the vertex tangent as
// interpolated, which PerturbNormal needs as it is. isFrontFace already accounts for mirrored instances (true =
// the side the triangle's winding says is outside).
struct SurfaceInputs
{
    float3 worldPosition;
    float3 normal;
    float4 tangent;        // w = bitangent sign
    float2 uv;
    float2 uvDdx;          // screen-space derivatives of uv, taken once outside any branch
    float2 uvDdy;
    float3 positionDdx;    // screen-space derivatives of worldPosition, as uv's are: for textures mapped in world space
    float3 positionDdy;
    float2 screenUv;
    float time;            // seconds, for animated surfaces
    float3 cameraPosition; // absolute, like worldPosition
    float3 viewDirection;  // unit length, from the surface toward the camera
    bool isFrontFace;
    float3 wind;           // the world's weather: metres per second, its direction and strength
    float coverage;        // the world's weather: how much of the sky clouds cover, 0 clear to 1 overcast
};

// What a surface hands back: what it is made of, in linear colour.
struct Surface
{
    float3 baseColor;
    float alpha;
    float3 normal;
    float roughness;    // perceptual, 0..1
    float metallic;
    float specular;     // a dielectric's reflectance: 0.5 is 4 % (most non-metals), 0 none
    float occlusion;    // baked or authored ambient occlusion, 0..1
    float3 emissive;    // pre-exposed: 1 reads as white whatever the lighting
    float alphaCutoff;
    uint shadingModel;  // ShadingDefault, ShadingClearCoat, ShadingCloth or ShadingSubsurface
    float2 modelData;   // what the shading model takes (Include/ShadingModel.hlsli)
};

Surface DefaultSurface(SurfaceInputs input)
{
    Surface surface;
    surface.baseColor = float3(0.8, 0.8, 0.8);
    surface.alpha = 1.0;
    surface.normal = input.normal;
    surface.roughness = 1.0;
    surface.metallic = 0.0;
    surface.specular = 0.5;
    surface.occlusion = 1.0;
    surface.emissive = float3(0.0, 0.0, 0.0);
    surface.alphaCutoff = 0.5;
    surface.shadingModel = ShadingDefault;
    surface.modelData = float2(0.0, 0.0);
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

// What was drawn behind the transparent surfaces (the scene before any of them, pre-exposed) at a pixel of the render
// target, kept inside the view, blurred the rougher roughness is (frosted glass). The renderer defines it; a
// transparent surface that calls it is refractive.
float3 SceneBehind(float2 pixel, float roughness);
#endif

// A tangent-space normal (x and y in -1..1, any length) into world space through the vertex frame as interpolated,
// not made orthogonal again per pixel: what MikkTSpace bakers (Blender, Substance, xNormal) assume, so a baked map
// shades as it did there. A mesh without tangents keeps its own normal.
float3 PerturbNormal(SurfaceInputs input, float3 tangentNormal)
{
    float3 bitangent = cross(input.normal, input.tangent.xyz) * input.tangent.w;
    float3 perturbed = NormalizeOrZero(tangentNormal.x * input.tangent.xyz + tangentNormal.y * bitangent + tangentNormal.z * input.normal);
    return dot(perturbed, perturbed) > 0.5 ? perturbed : input.normal;
}

// A normal map's normal in world space at the mesh's uv. Only x and y are read, z rebuilt so the normal is unit
// length, so two-channel maps (BC5) and three-channel ones serve alike; scale steepens or flattens the slopes.
float3 SampleNormal(SurfaceInputs input, TextureRef map, float scale)
{
    float2 slope = SampleTexture(input, map).xy * 2.0 - 1.0;
    float z = sqrt(saturate(1.0 - dot(slope, slope)));
    return PerturbNormal(input, float3(slope * scale, z));
}

#endif
