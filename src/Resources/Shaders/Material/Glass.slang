// Glass: a tinted pane the renderer draws after the lighting, forward, letting the picture behind through its
// colour. Its params are "color", whose alpha is how much the pane holds back, "roughness", "metalness" and
// "emissive".
module "Material/Glass";

import Surface.Surface;
import Material.IMaterial;

public struct Glass : IMaterial
{
    public static SurfaceOutputs Evaluate(SurfaceInputs inputs)
    {
        SurfaceOutputs surface;
        surface.baseColor = inputs.material.baseColor.rgb;
        surface.alpha = inputs.material.baseColor.a;
        surface.normal = inputs.normal;
        surface.roughness = inputs.material.roughness;
        surface.metalness = inputs.material.metalness;
        surface.emissive = inputs.material.emissive;
        surface.occlusion = 1.0;
        return surface;
    }
}
