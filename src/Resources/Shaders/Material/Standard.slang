// The standard material: metal-roughness PBR, as glTF defines it. A base colour, a roughness, a metalness and an
// emissive colour, each a factor that a map multiplies where the material names one. A normal map bends the surface
// it shades with.
module "Material/Standard";

import Core.Heap;
import Surface.Surface;
import Material.IMaterial;

public struct Standard : IMaterial
{
    public float4 color;
    public float roughness;
    public float metalness;
    public float3 emissive;
    // The colour map and the sampler it is read with. An id of 0 means none.
    public TextureRef colorMap;
    public SamplerRef colorMapSampler;
    // The tangent-space normal map and the sampler it is read with. An id of 0 means none. Its green channel points
    // up the surface, as the maps of glTF and OpenGL do.
    public TextureRef normalMap;
    public SamplerRef normalMapSampler;
    // The occlusion-roughness-metalness map and the sampler it is read with. An id of 0 means none. It holds the
    // occlusion in its red channel, the roughness in its green and the metalness in its blue, as glTF packs them,
    // and each multiplies the factor beside it.
    public TextureRef ormMap;
    public SamplerRef ormMapSampler;
    // The emissive map and the sampler it is read with. An id of 0 means none. It tints the emissive colour.
    public TextureRef emissiveMap;
    public SamplerRef emissiveMapSampler;

    public SurfaceOutputs Evaluate(SurfaceInputs inputs)
    {
        float4 base = color;
        if (colorMap.id != 0u)
            base *= colorMap.SampleGrad(colorMapSampler, inputs.uv, inputs.uvDdx, inputs.uvDdy);

        float3 normal = inputs.SampleNormal(normalMap, normalMapSampler);

        float3 orm = float3(1.0);
        if (ormMap.id != 0u)
            orm = ormMap.SampleGrad(ormMapSampler, inputs.uv, inputs.uvDdx, inputs.uvDdy).rgb;

        float3 glow = emissive;
        if (emissiveMap.id != 0u)
            glow *= emissiveMap.SampleGrad(emissiveMapSampler, inputs.uv, inputs.uvDdx, inputs.uvDdy).rgb;

        SurfaceOutputs surface;
        surface.baseColor = base.rgb;
        surface.alpha = base.a;
        surface.normal = normal;
        surface.roughness = roughness * orm.g;
        surface.metalness = metalness * orm.b;
        surface.emissive = glow;
        surface.occlusion = orm.r;
        return surface;
    }
}
