// The standard material: a base colour, tinted by a texture when the row names one, with roughness, metalness and an
// emissive colour from the row. Its params are "color", "roughness", "metalness", "emissive" and "baseColorTexture".
module "Material/Standard";

import Core.Heap;
import Surface.Surface;
import Material.IMaterial;

public struct Standard : IMaterial
{
    public static SurfaceOutputs Evaluate(SurfaceInputs inputs)
    {
        float4 base = inputs.material.baseColor;
        if (inputs.material.baseColorTexture != 0u)
        {
            TextureRef texture = { inputs.material.baseColorTexture };
            SamplerRef sampler = { inputs.sampler };
            base *= texture.SampleGrad(sampler, inputs.uv, inputs.uvDdx, inputs.uvDdy);
        }

        SurfaceOutputs surface;
        surface.baseColor = base.rgb;
        surface.alpha = base.a;
        surface.normal = inputs.normal;
        surface.roughness = inputs.material.roughness;
        surface.metalness = inputs.material.metalness;
        surface.emissive = inputs.material.emissive;
        surface.occlusion = 1.0;
        return surface;
    }
}
