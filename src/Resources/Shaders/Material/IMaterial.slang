// The material contract: a module implements it in a struct, a .mat names that module, and the resolve pass
// specialised to the struct turns each of its pixels into a surface. A material has no vertex stage: the cluster
// path put the triangle on screen, and the material says what it is made of there.
module "Material/IMaterial";

import Surface.Surface;

public interface IMaterial
{
    static SurfaceOutputs Evaluate(SurfaceInputs inputs);
}
