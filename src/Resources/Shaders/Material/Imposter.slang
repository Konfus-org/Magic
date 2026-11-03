// What an imposter's pixel is made of: the capture's albedo and normal as they were, rough and not metal, since a
// capture keeps no more of the surface than that.
module "Material/Imposter";

import Surface.Surface;

public SurfaceOutputs EvaluateImposter(float4 albedo, float3 normal)
{
    SurfaceOutputs surface;
    surface.baseColor = albedo.rgb;
    surface.alpha = albedo.a;
    surface.normal = normal;
    surface.roughness = 0.8;
    surface.metalness = 0.0;
    surface.emissive = float3(0.0);
    surface.occlusion = 1.0;
    return surface;
}
