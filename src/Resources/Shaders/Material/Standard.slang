// The standard material: a base colour, tinted by a colour map when it names one, with a roughness, a metalness and an
// emissive colour.
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

    public SurfaceOutputs Evaluate(SurfaceInputs inputs)
    {
        float4 base = color;
        if (colorMap.id != 0u)
            base *= colorMap.SampleGrad(colorMapSampler, inputs.uv, inputs.uvDdx, inputs.uvDdy);

        SurfaceOutputs surface;
        surface.baseColor = base.rgb;
        surface.alpha = base.a;
        surface.normal = inputs.normal;
        surface.roughness = roughness;
        surface.metalness = metalness;
        surface.emissive = emissive;
        surface.occlusion = 1.0;
        return surface;
    }
}
