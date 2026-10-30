// What a surface is written against, whatever renderer draws it. A surface is a module whose struct, named like its
// file, implements ISurface: the struct's fields are its parameters, and Evaluate says what it is made of at a point.
//
//   public struct Pbr : ISurface
//   {
//       [GiColor] Color color = { 0.8, 0.8, 0.8, 1.0 };
//       TextureRef colorMap = TextureNone;
//       public Surface Evaluate(SurfaceInputs input) { ... }
//   }
//
// A field may be a float, int, uint or bool, a vector of 2 to 4 of them, a Color or a TextureRef. Its
// initializer is its default, and a material (a .mat file) gives the values that differ, by the field's name. A field
// may carry a role for global illumination: [GiColor], [GiColorMap], [GiMetallic] or [GiEmissive]. A renderer reads the
// fields from the compiler's reflection and lays out each material's values as the struct is laid out, so the layout
// is never written by hand.
//
// A TextureRef names a texture the renderer resolved, or TextureNone for no texture: the surface's factor stands alone,
// as it does while the texture loads, which reads as white. Whether a texture is sRGB or linear is decided when it is
// loaded, so one Sample serves every map.
//
// A surface never deals with failure: a material whose file, shader or texture is broken is drawn with
// Surfaces/Failure instead, chosen by the renderer. It never lights itself either: it says what it is made of, and the
// renderer lights that (Lighting/Lights), its reflections included.

module "Core/Surface";

__exported import Core.Color;
__exported import Core.Heap;
__exported import Core.Math;
__exported import Core.Pass;
__exported import Core.Weather;

// The shading models a surface can ask for (Surface.shadingModel), and what each makes of Surface.modelData. A
// renderer keeps a pixel's model with it (two bits of a G-buffer) and lights it by Lighting/Lights.
//
//   Default     metallic-roughness: a Lambert diffuse and a GGX specular lobe. modelData unused.
//   ClearCoat   a second, smooth dielectric lobe over the default ones: car paint, lacquer, varnish.
//               modelData = (coat 0..1, coat roughness).
//   Cloth       the default lobes plus a sheen that brightens grazing angles: velvet, fabric.
//               modelData = (sheen 0..1, sheen roughness); the sheen is tinted by sqrt(baseColor).
//   Subsurface  thin translucency: light wraps past the terminator and passes through from behind, tinted by the
//               base colour: leaves, paper, thin cloth. modelData = (transmission 0..1, wrap 0..1).
public enum ShadingModel : uint
{
    Default,
    ClearCoat,
    Cloth,
    Subsurface,
}

// The roles a surface's field may carry for global illumination: what it says of the colour a surface bounces, of the
// texture that colour is read from, of how metallic it is and of the light it gives off.
[__AttributeUsage(_AttributeTargets.Var)]
public struct GiColorAttribute
{
}

[__AttributeUsage(_AttributeTargets.Var)]
public struct GiColorMapAttribute
{
}

[__AttributeUsage(_AttributeTargets.Var)]
public struct GiMetallicAttribute
{
}

[__AttributeUsage(_AttributeTargets.Var)]
public struct GiEmissiveAttribute
{
}

// What the renderer hands a surface, in world space. normal is unit length; tangent is the vertex tangent as
// interpolated, which PerturbNormal needs as it is. isFrontFace already accounts for mirrored instances (true = the
// side the triangle's winding says is outside).
public struct SurfaceInputs
{
    float3 worldPosition;
    float3 normal;
    float4 tangent;          // w = bitangent sign
    float2 uv;
    float2 uvDdx;            // screen-space derivatives of uv, taken once outside any branch
    float2 uvDdy;
    float3 positionDdx;      // screen-space derivatives of worldPosition, as uv's are: for textures mapped in world space
    float3 positionDdy;
    float2 screenUv;
    FrameConstants frame;    // the clocks: an animated surface moves by frame.worldSec, which stands still while paused
    float3 cameraPosition;   // absolute, like worldPosition
    float3 viewDirection;    // unit length, from the surface toward the camera
    bool isFrontFace;
    WeatherConstants weather;
    SamplerRef sampler;      // what the renderer reads materials' textures through

    // A texture at the mesh's own uv: what a map of a surface almost always wants.
    public float4 Sample(TextureRef texture)
    {
        return SampleGrad(texture, uv, uvDdx, uvDdy);
    }

    // A texture at any uv with that uv's own screen-space derivatives. Explicit gradients, so a branch may hold it,
    // where the implicit derivatives of Sample are undefined.
    public float4 SampleGrad(TextureRef texture, float2 at, float2 atDdx, float2 atDdy)
    {
        return SampleMaterialTexture(texture, sampler, at, atDdx, atDdy);
    }

    // A tangent-space normal (x and y in -1..1, any length) into world space through the vertex frame as interpolated,
    // not made orthogonal again per pixel: what MikkTSpace bakers (Blender, Substance, xNormal) assume, so a baked map
    // shades as it did there. A mesh without tangents keeps its own normal.
    public float3 PerturbNormal(float3 tangentNormal)
    {
        float3 bitangent = cross(normal, tangent.xyz) * tangent.w;
        float3 perturbed = NormalizeOrZero(tangentNormal.x * tangent.xyz + tangentNormal.y * bitangent + tangentNormal.z * normal);
        return dot(perturbed, perturbed) > 0.5 ? perturbed : normal;
    }

    // A normal map's normal in world space at the mesh's uv. Only x and y are read, z rebuilt so the normal is unit
    // length, so two-channel maps (BC5) and three-channel ones serve alike; scale steepens or flattens the slopes.
    public float3 SampleNormal(TextureRef map, float scale)
    {
        float2 slope = Sample(map).xy * 2.0 - 1.0;
        float z = sqrt(saturate(1.0 - dot(slope, slope)));
        return PerturbNormal(float3(slope * scale, z));
    }
}

// What a surface hands back: what it is made of, in linear colour.
public struct Surface
{
    float3 baseColor;
    float alpha;
    float3 normal;
    float roughness;         // perceptual, 0..1
    float metallic;
    float specular;          // a dielectric's reflectance: 0.5 is 4 % (most non-metals), 0 none
    float occlusion;         // baked or authored ambient occlusion, 0..1
    float3 emissive;         // pre-exposed: 1 reads as white whatever the lighting
    float alphaCutoff;
    ShadingModel shadingModel;
    float2 modelData;        // what the shading model takes
}

public interface ISurface
{
    Surface Evaluate(SurfaceInputs input);
}

// A matte grey with the mesh's own normal, which a surface starts from and changes what it says.
public Surface CreateDefaultSurface(SurfaceInputs input)
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
    surface.shadingModel = ShadingModel.Default;
    surface.modelData = float2(0.0, 0.0);
    return surface;
}

// A material's texture at uv with explicit gradients, white while it is none.
public float4 SampleMaterialTexture(TextureRef texture, SamplerRef sampler, float2 uv, float2 uvDdx, float2 uvDdy)
{
    [branch] if (!texture.isValid)
    {
        return float4(1.0, 1.0, 1.0, 1.0);
    }

    return texture.SampleGrad(sampler, uv, uvDdx, uvDdy);
}
