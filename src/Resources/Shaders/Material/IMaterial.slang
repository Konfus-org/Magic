// The material contract: a module implements it in a struct named for its file, whose fields are the material's params
// by name, at most 60 bytes laid out scalar. A TextureRef field is a texture param, read with the SamplerRef field named
// for it with "Sampler" after. A .mat names the module and sets the params, and the resolve pass specialised to the
// struct loads it from each of its pixels' rows and turns the pixel into a surface. A material has no vertex stage: the
// cluster path put the triangle on screen, and the material says what it is made of there. A translucent material,
// such as glass, declares `static const uint Translucent = 1;`: it draws over the lit picture, letting what is behind
// through.
module "Material/IMaterial";

import Surface.Surface;

public interface IMaterial
{
    SurfaceOutputs Evaluate(SurfaceInputs inputs);
}
