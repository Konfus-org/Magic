// Stripes of two colours across the surface's u, as many as "stripes" says: a material with params of its own.
module "Material/Stripes";

import Surface.Surface;
import Material.IMaterial;

public struct Stripes : IMaterial
{
    public float stripes;
    public float3 stripeColor;
    public float4 color;
    public float roughness;

    public SurfaceOutputs Evaluate(SurfaceInputs inputs)
    {
        float band = frac(inputs.uv.x * max(stripes, 1.0));

        SurfaceOutputs surface;
        surface.baseColor = band < 0.5 ? color.rgb : stripeColor;
        surface.alpha = 1.0;
        surface.normal = inputs.normal;
        surface.roughness = roughness;
        surface.metalness = 0.0;
        surface.emissive = float3(0.0, 0.0, 0.0);
        surface.occlusion = 1.0;
        return surface;
    }
}
