// The material a broken asset is drawn with: flat magenta, rough, unlit by anything but the sun, so what failed is
// seen at once wherever it is.
module "Material/Failure";

import Surface.Surface;
import Material.IMaterial;

public struct Failure : IMaterial
{
    // The kind of failure the failure assets set; every kind draws alike. A row's struct has a field to be loaded.
    public float kind;

    public SurfaceOutputs Evaluate(SurfaceInputs inputs)
    {
        SurfaceOutputs surface;
        surface.baseColor = float3(1.0, 0.0, 1.0);
        surface.alpha = 1.0;
        surface.normal = inputs.normal;
        surface.roughness = 1.0;
        surface.metalness = 0.0;
        surface.emissive = float3(0.0);
        surface.occlusion = 1.0;
        return surface;
    }
}
