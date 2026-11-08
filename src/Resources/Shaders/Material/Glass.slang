// Glass: a tinted pane the renderer draws after the lighting, forward, letting the picture behind through its
// colour, whose alpha is how much the pane holds back.
module "Material/Glass";

import Surface.Surface;
import Material.IMaterial;

public struct Glass : IMaterial
{
    public float4 color;
    public float roughness;
    public float metalness;
    public float3 emissive;

    public SurfaceOutputs Evaluate(SurfaceInputs inputs)
    {
        SurfaceOutputs surface;
        surface.baseColor = color.rgb;
        surface.alpha = color.a;
        surface.normal = inputs.normal;
        surface.roughness = roughness;
        surface.metalness = metalness;
        surface.emissive = emissive;
        surface.occlusion = 1.0;
        return surface;
    }
}
