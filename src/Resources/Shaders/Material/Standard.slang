// The standard material: metal-roughness PBR, as glTF defines it. A base colour, a roughness, a metalness and an
// emissive colour, each a factor that a map multiplies where the material names one. A normal map bends the surface
// it shades with.
module "Material/Standard";

import Core.Heap;
import Core.Math;
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
    // The mask map and the sampler it is read with. An id of 0 means none. It holds the occlusion in its red channel,
    // the roughness in its green and the metalness in its blue, as glTF packs them, and each multiplies the factor
    // beside it.
    public TextureRef maskMap;
    public SamplerRef maskMapSampler;

    public SurfaceOutputs Evaluate(SurfaceInputs inputs)
    {
        float4 base = color;
        if (colorMap.id != 0u)
            base *= colorMap.SampleGrad(colorMapSampler, inputs.uv, inputs.uvDdx, inputs.uvDdy);

        float3 normal = inputs.normal;
        if (normalMap.id != 0u)
        {
            float3 bent = normalMap.SampleGrad(normalMapSampler, inputs.uv, inputs.uvDdx, inputs.uvDdy).xyz * 2.0 - 1.0;
            float3 bitangent = cross(inputs.normal, inputs.tangent) * inputs.tangentSign;
            float3 shaded = inputs.tangent * bent.x + bitangent * bent.y + inputs.normal * bent.z;
            normal = NormalizeSafe(shaded, inputs.normal);
        }

        float3 mask = float3(1.0);
        if (maskMap.id != 0u)
            mask = maskMap.SampleGrad(maskMapSampler, inputs.uv, inputs.uvDdx, inputs.uvDdy).rgb;

        SurfaceOutputs surface;
        surface.baseColor = base.rgb;
        surface.alpha = base.a;
        surface.normal = normal;
        surface.roughness = roughness * mask.g;
        surface.metalness = metalness * mask.b;
        surface.emissive = emissive;
        surface.occlusion = mask.r;
        return surface;
    }
}
